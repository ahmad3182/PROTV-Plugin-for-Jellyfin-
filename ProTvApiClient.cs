using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PROTV.Plugin;

public sealed partial class ProTvApiClient
{
    private const string ChannelImageBaseUrl = "http://10.1.0.9:80/";
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProTvApiClient> _logger;
    private readonly Func<PluginConfiguration> _configurationFactory;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _tokenCredentialKey;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public ProTvApiClient(HttpClient httpClient, ILogger<ProTvApiClient> logger, Func<PluginConfiguration> configurationFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configurationFactory = configurationFactory;
    }

    private PluginConfiguration Configuration => _configurationFactory();

    public async Task<IReadOnlyList<ProTvChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvChannel>>(
            HttpMethod.Get,
            "api/Channels?Page=1&Count=1000&Order=Desc",
            cancellationToken);
        var channels = (response.Data?.Result ?? Array.Empty<ProTvChannel>())
            .Select(channel =>
            {
                channel.Logo = NormalizeImageUrl(channel.Logo);
                return channel;
            })
            .OrderBy(channel => channel.TitleEn ?? channel.TitleAr ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger.LogInformation("PROTV returned {ChannelCount} channels.", channels.Count);
        RememberKnownChannels(channels);
        LogChannelListIfChanged(channels);
        return channels;
    }

    // Keeps the latest PROTV channel list in the plugin settings so the dashboard page can show
    // every channel (including hidden ones) without a separate server endpoint.
    private static void RememberKnownChannels(IReadOnlyList<ProTvChannel> channels)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return;
            }

            var snapshot = channels
                .Select(channel => new ProTvKnownChannel
                {
                    Id = channel.Id,
                    Name = channel.TitleEn ?? channel.TitleAr ?? $"Channel {channel.Id}",
                    LogoUrl = channel.Logo ?? string.Empty
                })
                .OrderBy(channel => channel.Id)
                .ToArray();

            var current = plugin.Configuration.KnownChannels ?? Array.Empty<ProTvKnownChannel>();
            var unchanged = current.Length == snapshot.Length
                && current.Zip(snapshot).All(pair =>
                    pair.First.Id == pair.Second.Id
                    && pair.First.Name == pair.Second.Name
                    && pair.First.LogoUrl == pair.Second.LogoUrl);
            if (unchanged)
            {
                return;
            }

            plugin.Configuration.KnownChannels = snapshot;
            plugin.PersistConfiguration();
        }
        catch
        {
            // The dashboard listing is a convenience; never fail a channel fetch because of it.
        }
    }

    private static string? _lastLoggedChannelList;

    // Writes "number | PROTV id | name" for every channel (alphabetical, same order and numbers
    // Jellyfin shows) to the Jellyfin log whenever the list changes.
    private void LogChannelListIfChanged(IReadOnlyList<ProTvChannel> channels)
    {
        try
        {
            var lines = ProTvChannelOrdering.Sort(channels)
                .Select((channel, index) => $"{index + 1} | {channel.Id} | {ProTvChannelOrdering.DisplayName(channel)}");
            var list = string.Join(Environment.NewLine, lines);
            if (string.Equals(list, _lastLoggedChannelList, StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedChannelList = list;
            _logger.LogInformation("PROTV channel list (number | id | name):{NewLine}{ChannelList}", Environment.NewLine, list);
        }
        catch
        {
        }
    }

    private string? NormalizeImageUrl(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var image = imagePath.Trim();
        if (Uri.TryCreate(image, UriKind.Absolute, out var absoluteUri)
            && absoluteUri.Scheme is "http" or "https")
        {
            return absoluteUri.ToString();
        }

        if (!Uri.TryCreate(ChannelImageBaseUrl, UriKind.Absolute, out var baseUri)
            || !Uri.TryCreate(baseUri, image, out var relativeUri))
        {
            _logger.LogWarning("PROTV returned an invalid channel logo path.");
            return null;
        }

        return relativeUri.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Url, DateTime ExpiresUtc)> WatchCache = new();
    private static readonly TimeSpan WatchCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WatchUnavailableLifetime = TimeSpan.FromMinutes(10);
    private static DateTime _watchUnavailableUntilUtc = DateTime.MinValue;

    // Starting playback asks for the stream URL twice (media sources, then the live stream), and
    // when the Watch endpoint is missing each ask costs several failing HTTP calls. Multicast URLs
    // are static, so cache them briefly; failures and non-multicast URLs are never cached.
    public async Task<string> WatchChannelAsync(int channelId, CancellationToken cancellationToken)
    {
        if (WatchCache.TryGetValue(channelId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Url;
        }

        var url = await WatchChannelUncachedAsync(channelId, cancellationToken);
        if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
        {
            WatchCache[channelId] = (url, DateTime.UtcNow + WatchCacheLifetime);
        }

        return url;
    }

    private async Task<string> WatchChannelUncachedAsync(int channelId, CancellationToken cancellationToken)
    {
        // The server previously answered 404/405 for the Watch endpoint: skip straight to the
        // configured multicast address instead of repeating the failing requests on every play.
        if (DateTime.UtcNow < _watchUnavailableUntilUtc)
        {
            return GetConfiguredMulticastUrl(channelId);
        }

        try
        {
            var response = await SendAsync<ProTvResponse<string>>(
                HttpMethod.Post,
                $"api/Channels/Watch?Id={channelId}",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(response.Data))
            {
                return response.Data;
            }
        }
        catch (HttpRequestException exception) when (
            exception.Message.Contains("405", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var response = await SendAsync<ProTvResponse<string>>(
                    HttpMethod.Get,
                    $"api/Channels/Watch?Id={channelId}",
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(response.Data))
                {
                    return response.Data;
                }
            }
            catch (HttpRequestException retryException) when (
                retryException.Message.Contains("405", StringComparison.OrdinalIgnoreCase)
                || retryException.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("PROTV watch endpoint is unavailable for channel {ChannelId}; using multicast fallback.", channelId);
                _watchUnavailableUntilUtc = DateTime.UtcNow + WatchUnavailableLifetime;
            }
        }

        return GetConfiguredMulticastUrl(channelId);
    }

    // PROTV channel id -> multicast group (port 1234). Used when the Watch endpoint gives no URL.
    private static readonly Dictionary<int, string> ProTvMulticastMap = new()
    {
            [1] = "225.2.0.71",
            [2] = "225.2.0.135",
            [3] = "225.2.0.134",
            [4] = "225.2.0.133",
            [5] = "225.2.0.132",
            [6] = "225.2.0.175",
            [7] = "225.2.0.2",
            [9] = "225.2.0.136",
            [10] = "225.2.0.79",
            [11] = "225.2.0.173",
            [12] = "225.2.0.118",
            [13] = "225.2.0.116",
            [19] = "225.2.0.137",
            [20] = "225.2.0.129",
            [21] = "225.2.0.130",
            [22] = "225.2.0.120",
            [23] = "225.2.0.89",
            [24] = "225.2.0.206",
            [25] = "225.2.0.207",
            [26] = "225.2.0.205",
            [27] = "225.2.0.204",
            [30] = "225.2.0.200",
            [31] = "225.2.0.94",
            [32] = "225.2.0.43",
            [33] = "225.2.0.215",
            [34] = "225.2.0.40",
            [35] = "225.2.0.15",
            [36] = "225.2.0.13",
            [37] = "225.2.0.14",
            [38] = "225.2.0.150",
            [39] = "225.2.0.119",
            [42] = "225.2.0.114",
            [45] = "225.2.0.25",
            [48] = "225.2.0.91",
            [60] = "225.2.0.70",
            [62] = "225.2.0.52",
            [63] = "225.2.0.107",
            [64] = "225.2.0.117",
            [65] = "225.2.0.220",
            [66] = "225.2.0.223",
            [69] = "225.2.0.208",
            [70] = "225.2.0.90",
            [71] = "225.2.0.103",
            [75] = "225.2.0.105",
            [76] = "225.2.0.76",
            [77] = "225.2.0.110",
            [78] = "225.2.0.109",
            [80] = "225.2.0.113",
            [83] = "225.2.0.201",
            [88] = "225.2.0.6",
            [89] = "225.2.0.10",
            [90] = "225.2.0.9",
            [91] = "225.2.0.31",
            [92] = "225.2.0.5",
            [93] = "225.2.0.7",
            [94] = "225.2.0.4",
            [95] = "225.2.0.3",
            [96] = "225.2.0.219",
            [97] = "225.2.0.1",
            [100] = "225.2.0.218",
            [102] = "225.2.0.75",
            [105] = "225.2.0.24",
            [106] = "225.2.0.232",
            [107] = "225.2.0.67",
            [108] = "225.2.0.8",
            [109] = "225.2.0.69",
            [110] = "225.2.0.78",
            [112] = "225.2.0.34",
            [114] = "225.2.0.122",
            [115] = "225.2.0.74",
            [116] = "225.2.0.115",
            [117] = "225.2.0.50",
            [118] = "225.2.0.30",
            [120] = "225.2.0.54",
            [125] = "225.2.0.95",
            [128] = "225.2.0.111",
            [129] = "225.2.0.230",
            [130] = "225.2.0.233",
            [131] = "225.2.0.209",
            [132] = "225.2.0.16",
            [133] = "225.2.0.210",
            [135] = "225.2.0.236",
            [136] = "225.2.0.229",
            [137] = "225.2.0.238",
            [138] = "225.2.0.237",
            [139] = "225.2.0.203",
            [142] = "225.2.0.92",
            [143] = "225.2.0.125",
            [144] = "225.2.0.123",
            [148] = "225.2.0.225",
            [150] = "225.2.0.224",
            [151] = "225.2.0.227",
            [156] = "225.2.0.211",
            [159] = "225.2.0.235",
            [160] = "225.2.0.231",
            [161] = "225.2.0.108",
            [178] = "225.2.0.121",
            [180] = "225.2.0.174",
            [182] = "225.2.0.213",
            [191] = "225.2.0.172",
            [194] = "225.2.0.202",
            [198] = "225.2.0.131",
            [201] = "225.2.0.93",
            [205] = "225.2.0.124",
            [207] = "225.2.0.226",
            [212] = "225.2.0.126",
            [213] = "225.2.0.212",
            [214] = "225.2.0.21",
            [215] = "225.2.0.20",
            [216] = "225.2.0.23",
            [217] = "225.2.0.22",
            [218] = "225.2.0.65",
            [220] = "225.2.0.72",
            [221] = "225.2.0.199",
            [222] = "225.2.0.101",
            [223] = "225.2.0.221",
            [224] = "225.2.0.222",
            [225] = "225.2.0.102",
            [226] = "225.2.0.217",
            [227] = "225.2.0.239"
    };

    private string GetConfiguredMulticastUrl(int channelId)
    {
        var channelMappings = ProTvMulticastMap;

        if (channelMappings.TryGetValue(channelId, out var multicastAddress))
        {
            return $"udp://@{multicastAddress}:1234";
        }

        _logger.LogWarning("PROTV channel {ChannelId} has no stream URL from the server and no multicast mapping.", channelId);
        throw new InvalidOperationException($"Channel {channelId} is not working: no stream address is available for it.");
    }

    public async Task<IReadOnlyList<ProTvEpgChannel>> GetEpgAsync(CancellationToken cancellationToken)
    {
        // The live API answers with EPGDataResponseDto: channelTitleEn + epgItems, and each item
        // has a local "showTime" plus a duration in minutes. The response also carries the server's
        // local and UTC clock, which lets us convert its local programme times correctly even when
        // Jellyfin runs in another time zone.
        var response = await SendAsync<ProTvEpgResponse>(
            HttpMethod.Get,
            "api/Channels/EPGs",
            cancellationToken);
        var channels = response.Data ?? Array.Empty<ProTvEpgChannel>();
        var serverOffset = ServerOffset(response);
        foreach (var channel in channels)
        {
            foreach (var program in channel.Programs ?? Array.Empty<ProTvEpgItem>())
            {
                program.ServerOffset = serverOffset;
            }
        }

        return channels;
    }

    // serverTimeLocal carries the service's UTC offset; subtracting the two DateTimeOffsets
    // directly would compare instants (both are the same instant) and produce zero.
    internal static TimeSpan? ServerOffset(ProTvEpgResponse response)
    {
        if (response.ServerTimeLocal is not { } local || response.ServerTimeUtc is not { } utc)
        {
            return null;
        }

        return TimeSpan.FromMinutes(Math.Round((local.Offset - utc.Offset).TotalMinutes));
    }

    private Task<T> SendAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken)
        => SendAsync<T>(method, path, addDeviceHeaders: false, acceptLanguage: null, cancellationToken);

    // One try. Retries and the circuit breaker wrap it (ProTvApiClient.Resilience.cs).
    private async Task<T> SendOnceAsync<T>(
        HttpMethod method,
        string path,
        bool addDeviceHeaders,
        string? acceptLanguage,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        using var response = await SendAuthorizedAsync(method, path, addDeviceHeaders, acceptLanguage, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // The saved token expired: authenticate once more and replay the request.
            _accessToken = null;
            _refreshToken = null;
            _tokenCredentialKey = null;
            await EnsureAuthenticatedAsync(cancellationToken);
            using var retry = await SendAuthorizedAsync(method, path, addDeviceHeaders, acceptLanguage, cancellationToken);
            retry.EnsureSuccessStatusCode();
            return await ReadAsync<T>(retry, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("PROTV returned an empty response.");
    }

    private Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
        => SendAuthorizedAsync(method, path, addDeviceHeaders: false, acceptLanguage: null, cancellationToken);

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        bool addDeviceHeaders,
        string? acceptLanguage,
        CancellationToken cancellationToken)
    {
        // Device detection includes a public-IP lookup and interface enumeration. It used to run
        // synchronously on every request thread; now it is awaited off the request thread and
        // shared by concurrent callers.
        var device = addDeviceHeaders
            ? await ProTvDeviceInfo.DetectAsync(Configuration, cancellationToken).ConfigureAwait(false)
            : null;

        var request = new HttpRequestMessage(method, BuildUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (device is not null)
        {
            // The VOD endpoints are the ones the desktop app calls with its full device header set;
            // the live TV endpoints work without them, so only VOD asks for them.
            AddDeviceHeaders(request, device, acceptLanguage ?? "en-US");
        }
        else if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        return await SendAndDisposeRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static string BuildCredentialKey(PluginConfiguration configuration)
    {
        return string.Join("\n", configuration.ApiBaseUrl?.Trim() ?? string.Empty, configuration.Username?.Trim() ?? string.Empty, configuration.PasswordValue);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var configuration = Configuration;
        var credentialKey = BuildCredentialKey(configuration);

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            // Saved credentials or URL changed since the cached tokens were issued: throw the tokens away.
            if (_tokenCredentialKey is not null && !string.Equals(_tokenCredentialKey, credentialKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("PROTV settings changed; discarding cached tokens.");
                _accessToken = null;
                _refreshToken = null;
            }

            if (!string.IsNullOrWhiteSpace(_accessToken))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_refreshToken))
            {
                try
                {
                    await RefreshAsync(cancellationToken);
                    _tokenCredentialKey = credentialKey;
                    return;
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogInformation(exception, "PROTV token refresh failed; logging in with the saved credentials.");
                    _accessToken = null;
                    _refreshToken = null;
                }
            }

            var username = configuration.Username?.Trim();
            var password = configuration.PasswordValue;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("PROTV username and password are not saved. Enter them in Dashboard > Plugins > PROTV and press Save.");
            }

            var device = await ProTvDeviceInfo.DetectAsync(configuration, cancellationToken).ConfigureAwait(false);
            var payload = new ProTvLoginRequest(username, password, device.IpAddress);
            var json = JsonSerializer.Serialize(payload);
            _logger.LogInformation("PROTV logging in with the saved credentials (username and password are configured).");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/Account/Login"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddDeviceHeaders(request, device);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<ProTvResponse<ProTvLoginData>>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("PROTV returned an empty login response.");
            SetTokens(login.Data);
            _tokenCredentialKey = credentialKey;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var device = await ProTvDeviceInfo.DetectAsync(Configuration, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/Account/RefreshToken"));
        request.Headers.Add("refresh-token", _refreshToken);
        AddDeviceHeaders(request, device);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<ProTvResponse<ProTvLoginData>>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("PROTV returned an empty refresh response.");
        SetTokens(login.Data);
    }

    private void SetTokens(ProTvLoginData? data)
    {
        _accessToken = NormalizeToken(data?.Profile?.Token ?? data?.Token ?? data?.AccessToken);
        _refreshToken = NormalizeToken(data?.Client?.RefreshToken ?? data?.RefreshToken);
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("PROTV did not return an access token.");
        }
    }

    private static string? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim()
            : token.Trim();
    }

    private void AddDeviceHeaders(HttpRequestMessage request, ProTvDeviceInfo device, string acceptLanguage = "en-US")
    {
        request.Headers.Add("Accept-Language", acceptLanguage);
        request.Headers.Add("device-type", "Desktop");
        request.Headers.Add("device-os", "Windows10");
        request.Headers.Add("app-version", Configuration.AppVersion);
        request.Headers.Add(
            "app-id",
            string.IsNullOrWhiteSpace(Configuration.AppId)
                ? "ca88f40a-3056-4c52-9614-8d75a3b0b178"
                : Configuration.AppId);
        request.Headers.Add("device-ip", device.IpAddress);
        request.Headers.Add("device-mac-address", device.MacAddress);
        request.Headers.Add("router-mac-address", device.RouterMacAddress);
        request.Headers.Add("device-model", device.Model);
        request.Headers.Add("push-token", string.Empty);
        request.Headers.Add("notification-token", string.Empty);
    }

    private Uri BuildUri(string path)
    {
        return new Uri(new Uri(Configuration.ApiBaseUrl.TrimEnd('/') + "/"), path);
    }
}

internal sealed record ProTvDeviceInfo(string IpAddress, string MacAddress, string RouterMacAddress, string Model)
{
    private static readonly object DeviceCacheLock = new();
    private static readonly object DeviceTaskLock = new();
    private static string? _deviceCacheKey;
    private static ProTvDeviceInfo? _deviceCache;
    private static DateTime _deviceCacheUtc = DateTime.MinValue;
    private static Task<ProTvDeviceInfo>? _deviceTask;
    private static string? _deviceTaskKey;
    private static readonly TimeSpan DeviceCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly HttpClient PublicIpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    // Device detection runs for VOD API requests (headers); cache the result so playback start
    // does not repeat interface enumeration, gateway lookup and ARP reads. It is async and
    // single-flight so a slow public-IP lookup cannot block an ASP.NET request thread.
    public static async Task<ProTvDeviceInfo> DetectAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var key = DeviceCacheKey(configuration);
        lock (DeviceCacheLock)
        {
            if (_deviceCache is not null
                && string.Equals(_deviceCacheKey, key, StringComparison.Ordinal)
                && DateTime.UtcNow - _deviceCacheUtc < DeviceCacheLifetime)
            {
                return _deviceCache;
            }
        }

        Task<ProTvDeviceInfo> task;
        lock (DeviceTaskLock)
        {
            if (_deviceTask is null || !string.Equals(_deviceTaskKey, key, StringComparison.Ordinal))
            {
                _deviceTaskKey = key;
                _deviceTask = Task.Run(() => DetectUncached(configuration), CancellationToken.None);
            }

            task = _deviceTask;
        }

        var detected = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (DeviceCacheLock)
        {
            _deviceCacheKey = key;
            _deviceCache = detected;
            _deviceCacheUtc = DateTime.UtcNow;
        }

        // Do not memoise a completed detection task forever: the five-minute cache expiry must
        // actually re-enumerate interfaces and the gateway after a DHCP/NIC change.
        lock (DeviceTaskLock)
        {
            if (ReferenceEquals(_deviceTask, task))
            {
                _deviceTask = null;
                _deviceTaskKey = null;
            }
        }

        return detected;
    }

    private static string DeviceCacheKey(PluginConfiguration configuration)
        => string.Join(
            "|",
            configuration.ApiBaseUrl,
            configuration.DeviceIpAddress,
            configuration.DeviceMacAddress,
            configuration.RouterMacAddress,
            configuration.DeviceModel);

    private static ProTvDeviceInfo DetectUncached(PluginConfiguration configuration)
    {
        try
        {
            var ipAddress = DetectIpAddress(configuration.ApiBaseUrl, configuration.DeviceIpAddress);
            var macAddress = DetectMacAddress(configuration.DeviceMacAddress);
            var gateway = DetectGateway();
            var routerMacAddress = string.IsNullOrWhiteSpace(configuration.RouterMacAddress)
                ? DetectGatewayMac(gateway)
                : configuration.RouterMacAddress;
            var model = string.IsNullOrWhiteSpace(configuration.DeviceModel)
                || configuration.DeviceModel.Equals("Jellyfin Server", StringComparison.OrdinalIgnoreCase)
                ? Environment.MachineName
                : configuration.DeviceModel;

            return new ProTvDeviceInfo(ipAddress, macAddress, routerMacAddress, model);
        }
        catch
        {
            // A probe failure must not stop login or VOD browsing. Send the configured values.
            return new ProTvDeviceInfo(
                configuration.DeviceIpAddress ?? string.Empty,
                configuration.DeviceMacAddress ?? string.Empty,
                configuration.RouterMacAddress ?? string.Empty,
                string.IsNullOrWhiteSpace(configuration.DeviceModel) ? Environment.MachineName : configuration.DeviceModel);
        }
    }

    private static readonly object IpCacheLock = new();
    private static string? _cachedIp;
    private static DateTime _cachedIpUtc = DateTime.MinValue;
    private static readonly TimeSpan IpCacheLifetime = TimeSpan.FromMinutes(10);

    private static string DetectIpAddress(string apiBaseUrl, string fallback)
    {
        // An explicitly configured address always wins and needs no network calls.
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback.Trim();
        }

        lock (IpCacheLock)
        {
            if (_cachedIp is not null && DateTime.UtcNow - _cachedIpUtc < IpCacheLifetime)
            {
                return _cachedIp;
            }
        }

        var detected = DetectIpAddressUncached(apiBaseUrl);
        lock (IpCacheLock)
        {
            _cachedIp = detected;
            _cachedIpUtc = DateTime.UtcNow;
        }

        return detected;
    }

    private static string DetectIpAddressUncached(string apiBaseUrl)
    {
        try
        {
            var publicIp = PublicIpClient.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
            if (IPAddress.TryParse(publicIp, out var parsedPublicIp) && parsedPublicIp.AddressFamily == AddressFamily.InterNetwork)
            {
                return parsedPublicIp.ToString();
            }
        }
        catch
        {
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new Uri(apiBaseUrl).Host, 82);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DetectMacAddress(string fallback)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderBy(item => item.Id)
            .FirstOrDefault();
        var address = adapter?.GetPhysicalAddress().GetAddressBytes();
        return address is { Length: > 0 }
            ? string.Join(":", address.Select(value => value.ToString("X2")))
            : fallback ?? string.Empty;
    }

    private static string DetectGateway()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().GatewayAddresses)
            .Select(item => item.Address)
            .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? string.Empty;
    }

    private static string DetectGatewayMacLinux(string gateway)
    {
        try
        {
            // /proc/net/arp columns: IP address, HW type, Flags, HW address, Mask, Device
            foreach (var line in File.ReadLines("/proc/net/arp").Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4
                    && parts[0] == gateway
                    && parts[3] != "00:00:00:00:00:00")
                {
                    return parts[3].ToUpperInvariant();
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string DetectGatewayMac(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return string.Empty;
        }

        if (OperatingSystem.IsLinux())
        {
            return DetectGatewayMacLinux(gateway);
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "arp.exe",
                Arguments = $"-a {gateway}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            var output = process?.StandardOutput.ReadToEnd() ?? string.Empty;
            process?.WaitForExit(2000);
            var match = System.Text.RegularExpressions.Regex.Match(output, @"([0-9a-f]{2}(?:-[0-9a-f]{2}){5})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value.Replace('-', ':').ToUpperInvariant() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

public sealed record ProTvLoginRequest(
    [property: JsonPropertyName("UserName")] string UserName,
    [property: JsonPropertyName("Password")] string Password,
    [property: JsonPropertyName("IpAddress")] string IpAddress);

public class ProTvResponse<T>
{
    public T? Data { get; set; }
    public bool Status { get; set; }
    public string? Message { get; set; }
}

public sealed class ProTvListResponse<T> : ProTvResponse<ProTvDataList<T>>;

public sealed class ProTvDataList<T>
{
    public IReadOnlyList<T>? Result { get; set; }
    public int PageCount { get; set; }
    public int PageSize { get; set; }
    public int CurrentPage { get; set; }
    public int Total { get; set; }
}

public sealed class ProTvLoginData
{
    public ProTvProfile? Profile { get; set; }
    public ProTvClient? Client { get; set; }
    public string? Token { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
}

public sealed class ProTvProfile
{
    public string? Token { get; set; }
}

public sealed class ProTvClient
{
    public string? RefreshToken { get; set; }
}

public sealed class ProTvChannel
{
    public int Id { get; set; }
    public int FixedNumber { get; set; }
    public string? TitleEn { get; set; }
    public string? TitleAr { get; set; }
    public string? Logo { get; set; }
    public string? LauncherURL { get; set; }
    public string? WebURL { get; set; }
}

public sealed class ProTvEpgResponse : ProTvResponse<IReadOnlyList<ProTvEpgChannel>>
{
    [JsonPropertyName("serverTimeLocal")]
    public DateTimeOffset? ServerTimeLocal { get; set; }

    [JsonPropertyName("serverTimeUTC")]
    public DateTimeOffset? ServerTimeUtc { get; set; }
}

public sealed class ProTvEpgChannel
{
    [JsonPropertyName("channelId")]
    public int ChannelId { get; set; }

    [JsonPropertyName("channelTitleEn")]
    public string? ChannelName { get; set; }

    [JsonPropertyName("channelTitleAr")]
    public string? ChannelNameAr { get; set; }

    // PROTV calls this array "epgItems"; keep the older property name for the callers.
    [JsonPropertyName("epgItems")]
    public IReadOnlyList<ProTvEpgItem>? Programs { get; set; }
}

public sealed class ProTvEpgItem
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("showTime")]
    public DateTime? ShowTime { get; set; }

    [JsonPropertyName("day")]
    public DateTime? Day { get; set; }

    // "00:30:00" on the wire; kept as text because System.Text.Json cannot bind a time-only
    // string to DateTime.
    [JsonPropertyName("startTime")]
    public string? StartTimeText { get; set; }

    // Minutes on the wire ("120"), or a time span in some service versions.
    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    // Set by GetEpgAsync from serverTimeLocal - serverTimeUTC so programme times are converted
    // correctly even when the Jellyfin host uses a different time zone.
    [JsonIgnore]
    public TimeSpan? ServerOffset { get; set; }

    [JsonIgnore]
    public DateTime StartTimeUtc
    {
        get
        {
            var local = LocalStartTime();
            if (local == DateTime.MinValue)
            {
                return DateTime.MinValue;
            }

            var offset = ServerOffset ?? TimeZoneInfo.Local.GetUtcOffset(local);
            return new DateTimeOffset(local, offset).UtcDateTime;
        }
    }

    [JsonIgnore]
    public DateTime EndTimeUtc
    {
        get
        {
            var start = StartTimeUtc;
            return start == DateTime.MinValue ? DateTime.MinValue : start.AddMinutes(DurationMinutes());
        }
    }

    private DateTime LocalStartTime()
    {
        if (ShowTime is { } showTime)
        {
            return DateTime.SpecifyKind(showTime, DateTimeKind.Unspecified);
        }

        if (Day is { } day
            && TimeSpan.TryParse(StartTimeText, CultureInfo.InvariantCulture, out var start))
        {
            return DateTime.SpecifyKind(day.Date + start, DateTimeKind.Unspecified);
        }

        return DateTime.MinValue;
    }

    private int DurationMinutes()
    {
        if (int.TryParse(Duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes > 0)
        {
            return minutes;
        }

        if (TimeSpan.TryParse(Duration, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero)
        {
            return Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        }

        return 30;
    }
}

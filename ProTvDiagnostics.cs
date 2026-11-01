using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PROTV.Plugin;

// What the dashboard sends to try a login: the values on screen, which may not be saved yet.
public sealed class ProTvTestRequest
{
    public string? ApiBaseUrl { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? AppId { get; set; }

    public string? DeviceModel { get; set; }

    public string? DeviceIpAddress { get; set; }

    public string? DeviceMacAddress { get; set; }

    public string? RouterMacAddress { get; set; }
}

public sealed class ProTvTestResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("channelCount")]
    public int ChannelCount { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }
}

// The last warnings and errors the plugin logged, kept in memory for the diagnostic report.
internal static class ProTvDiagnosticsLog
{
    private const int Capacity = 40;
    private static readonly ConcurrentQueue<string> Entries = new();

    public static void Record(LogLevel level, string message, Exception? exception)
    {
        var text = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level}: {message}";
        if (exception is not null)
        {
            text += $" ({exception.GetType().Name}: {exception.Message})";
        }

        Entries.Enqueue(text.Length > 400 ? text[..400] : text);
        while (Entries.Count > Capacity && Entries.TryDequeue(out _))
        {
        }
    }

    public static string[] Snapshot() => Entries.ToArray();
}

// Passes everything on to Jellyfin's logger and keeps a copy of the warnings and errors.
internal sealed class ProTvRecordingLogger : ILogger<ProTvApiClient>
{
    private readonly ILogger _inner;

    public ProTvRecordingLogger(ILogger inner)
    {
        _inner = inner;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning || _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);
        if (logLevel >= LogLevel.Warning)
        {
            ProTvDiagnosticsLog.Record(logLevel, formatter(state, exception), exception);
        }
    }
}

internal static class ProTvDiagnostics
{
    private static readonly SemaphoreSlim TestGate = new(1, 1);

    // ---- connection test ----

    public static async Task<ProTvTestResult> TestConnectionAsync(ProTvTestRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Fail("No settings were sent.");
        }

        var url = (request.ApiBaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Fail("The API base URL is not a valid web address. It looks like http://10.1.0.8:82.");
        }

        // The dashboard leaves the password box empty when the saved password should be reused.
        // Only fall back when the username is unchanged, so a renamed account cannot accidentally
        // be tested with the old account's password.
        var password = request.Password;
        if (string.IsNullOrEmpty(password))
        {
            var saved = Plugin.Instance?.Configuration;
            if (saved is not null
                && !string.IsNullOrWhiteSpace(saved.Username)
                && string.Equals(saved.Username.Trim(), request.Username?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                password = saved.PasswordValue;
            }
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(password))
        {
            return Fail("Enter the username and password first.");
        }

        if (!await TestGate.WaitAsync(0, cancellationToken))
        {
            return Fail("A test is already running. Wait for it to finish.");
        }

        var defaults = new PluginConfiguration();
        var configuration = new PluginConfiguration
        {
            ApiBaseUrl = url,
            Username = request.Username.Trim(),
            Password = password,
            AppId = string.IsNullOrWhiteSpace(request.AppId) ? defaults.AppId : request.AppId.Trim(),
            DeviceModel = string.IsNullOrWhiteSpace(request.DeviceModel) ? defaults.DeviceModel : request.DeviceModel.Trim(),
            DeviceIpAddress = (request.DeviceIpAddress ?? string.Empty).Trim(),
            DeviceMacAddress = (request.DeviceMacAddress ?? string.Empty).Trim(),
            RouterMacAddress = (request.RouterMacAddress ?? string.Empty).Trim(),
            AppVersion = Plugin.Instance?.Configuration.AppVersion ?? defaults.AppVersion
        };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            // A client of its own: nothing here touches the login the plugin is using.
            var client = new ProTvApiClient(http, NullLogger<ProTvApiClient>.Instance, () => configuration);
            var clock = Stopwatch.StartNew();
            var channels = await client.GetChannelsAsync(timeout.Token);
            clock.Stop();

            return new ProTvTestResult
            {
                Ok = true,
                ChannelCount = channels.Count,
                ElapsedMs = clock.ElapsedMilliseconds,
                Message = $"Connected. PROTV answered with {channels.Count} channels in {clock.Elapsed.TotalSeconds:0.0} s."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"No answer from {uri.Host} within 20 seconds. Check that this server can reach the PROTV network (and, in Docker, that it uses host networking).");
        }
        catch (HttpRequestException exception)
        {
            return Fail($"Could not reach {uri.Host}:{uri.Port}. {Redact(exception.InnerException?.Message ?? exception.Message, configuration)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail($"PROTV did not accept the request: {Redact(exception.Message, configuration)}");
        }
        finally
        {
            TestGate.Release();
        }
    }

    private static ProTvTestResult Fail(string message) => new() { Ok = false, Message = message };

    // ---- diagnostic report ----

    public static string BuildReport(IServerApplicationHost? host)
    {
        var configuration = Plugin.Instance?.Configuration;
        var builder = new StringBuilder();

        void Line(string text = "") => builder.AppendLine(text);
        string Set(string? value) => string.IsNullOrWhiteSpace(value) ? "not set" : "set";

        Line("PROTV diagnostic report");
        Line($"Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        Line();
        Line("== Versions");
        Line($"PROTV plugin: {Plugin.Instance?.Version}");
        Line($"Jellyfin: {host?.ApplicationVersionString ?? "unknown"}");
        Line($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line($".NET: {RuntimeInformation.FrameworkDescription}");
        Line($"In a container: {(File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "yes" : "no")}");

        Line();
        Line("== PROTV service");
        try
        {
            // No probe: the report only reports what the circuit breaker already knows, it sends nothing.
            var service = ProTvSharedClient.Get().GetServiceStatus(allowProbe: false);
            Line($"State: {service.State} since {service.Since:yyyy-MM-dd HH:mm:ss}Z");
        }
        catch (Exception exception)
        {
            Line($"Could not read: {exception.Message}");
        }

        Line();
        Line("== Companion plugins");
        try
        {
            var report = ProTvRequirements.Check();
            Line(report.Describe());
            Line($"Require companion plugins: {configuration?.RequireCompanionPlugins ?? true}");
        }
        catch (Exception exception)
        {
            Line($"Could not check: {exception.Message}");
        }

        Line();
        Line("== Settings (secrets are never included)");
        if (configuration is null)
        {
            Line("The plugin settings are not loaded.");
        }
        else
        {
            Line($"API address: {configuration.ApiBaseUrl}");
            Line($"Username: {Set(configuration.Username)}");
            Line($"Password: {Set(configuration.PasswordValue)}");
            Line($"Secret encryption: {(ProTvSecretStore.IsConfigured ? "key available" : "key unavailable; secrets may be stored in clear text")}");
            Line($"Device model: {configuration.DeviceModel}");
            Line($"Device IP / device MAC / router MAC: {Set(configuration.DeviceIpAddress)} / {Set(configuration.DeviceMacAddress)} / {Set(configuration.RouterMacAddress)}");
            Line($"Multicast interface: {(string.IsNullOrWhiteSpace(configuration.MulticastInterfaceAddress) ? "automatic" : "set")}");
            Line($"Stream analysis time: {configuration.StreamAnalyzeDurationMs} ms");
            Line($"API resilience (retries and pause): {(configuration.LiveApiResilience ? "on" : "off")}");
            Line($"Home page tab: {(configuration.FrontpageTabEnabled ? "on" : "off")} (registered: {ProTvCustomTabs.IsRegistered()})");
            Line($"Channels known: {configuration.KnownChannels?.Length ?? 0}, with overrides: {configuration.ChannelOverrides?.Length ?? 0}");
            Line($"VOD: films {(configuration.VodMoviesEnabled ? "on" : "off")}, series {(configuration.VodSeriesEnabled ? "on" : "off")}, stream address {Set(configuration.VodStreamBaseUrl)}");
        }

        Line();
        Line("== VOD library");
        try
        {
            var vod = ProTvVodData.GetStatus();
            Line($"Libraries registered: films {vod.LibraryRegistered}, series {vod.SeriesLibraryRegistered}");
            Line($"Titles: {vod.Movies} films, {vod.Series} series, {vod.Episodes} episodes, {vod.Subtitles} subtitles");
            Line($"Downloaded: {Bytes(vod.AssetBytes)} of {(vod.AssetBudgetBytes > 0 ? Bytes(vod.AssetBudgetBytes) : "unlimited")}{(vod.BudgetReached ? " (limit reached)" : string.Empty)}");
            Line($"Last sync: {vod.LastSyncUtc ?? "never"}");
            Line($"Last sync error: {vod.LastError ?? "none"}");
        }
        catch (Exception exception)
        {
            Line($"Could not read: {exception.Message}");
        }

        Line();
        Line("== Network (addresses shortened)");
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => Mask(item.Address))
                    .ToArray();
                if (addresses.Length > 0)
                {
                    Line($"{nic.Name} ({nic.NetworkInterfaceType}), multicast: {(nic.SupportsMulticast ? "yes" : "no")}, {string.Join(", ", addresses)}");
                }
            }
        }
        catch (Exception exception)
        {
            Line($"Could not read: {exception.Message}");
        }

        Line();
        Line("== Recent warnings and errors from PROTV");
        var recent = ProTvDiagnosticsLog.Snapshot();
        if (recent.Length == 0)
        {
            Line("none since the server started");
        }
        else
        {
            foreach (var entry in recent)
            {
                Line(entry);
            }
        }

        return Redact(builder.ToString(), configuration);
    }

    // The report and the error texts leave the server (people paste them into issues), so the
    // account and the link secret are taken out of them whatever the text says.
    private static string Redact(string text, PluginConfiguration? configuration)
    {
        if (configuration is null)
        {
            return text;
        }

        foreach (var secret in new[]
        {
            configuration.Password,
            configuration.PasswordValue,
            configuration.Username,
            configuration.VodStreamSecret,
            configuration.VodStreamSecretValue
        })
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static string Mask(IPAddress address)
    {
        var parts = address.ToString().Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}.x" : "x";
    }

    private static string Bytes(long bytes)
        => bytes >= 1L << 30
            ? $"{bytes / (double)(1L << 30):0.0} GB"
            : $"{bytes / (double)(1L << 20):0.0} MB";
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PROTV.Plugin;

// What the dashboard shows under "Update". Never contains tokens or credentials.
public sealed class ProTvUpdateInfo
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("releaseUrl")]
    public string? ReleaseUrl { get; set; }

    [JsonPropertyName("checkedUtc")]
    public DateTime CheckedUtc { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// Reads the newest public GitHub release for the repository configured on the dashboard.
// The check is cached for six hours so opening the dashboard does not hammer GitHub, and all
// failures are reported as a short message instead of breaking the page.
internal static class ProTvUpdates
{
    private static readonly object Gate = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static ProTvUpdateInfo? _cached;
    private static DateTime _cachedUtc = DateTime.MinValue;

    public static async Task<ProTvUpdateInfo> CheckAsync(bool force, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        var repository = configuration?.UpdateRepository?.Trim() ?? string.Empty;
        var current = Normalize(Plugin.Instance?.Version?.ToString()) ?? "0.0.0.0";
        var configured = IsValidRepository(repository);

        if (!configured)
        {
            return new ProTvUpdateInfo
            {
                Configured = false,
                CurrentVersion = current,
                CheckedUtc = DateTime.UtcNow
            };
        }

        lock (Gate)
        {
            if (!force && _cached is not null && DateTime.UtcNow - _cachedUtc < CacheLifetime)
            {
                return _cached;
            }
        }

        var result = new ProTvUpdateInfo
        {
            Configured = true,
            CurrentVersion = current,
            CheckedUtc = DateTime.UtcNow
        };

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"PROTV.Plugin/{current}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{repository}/releases/latest",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                result.Error = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "The repository has no public release yet."
                    : $"GitHub answered {(int)response.StatusCode}. Try again later.";
                return Remember(result);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
            var latest = Normalize(tag);
            var releaseUrl = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;

            result.LatestVersion = latest ?? tag;
            result.ReleaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? null : releaseUrl;
            if (latest is not null)
            {
                result.UpdateAvailable = Compare(latest, current) > 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            result.Error = "Could not reach GitHub. Check the server's internet access.";
        }

        return Remember(result);
    }

    internal static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && parts.All(part => part.Length > 0
                && part.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'));
    }

    internal static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var text = version.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed))
        {
            return null;
        }

        return parsed.Revision >= 0
            ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}.{parsed.Revision}"
            : $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}.0";
    }

    internal static int Compare(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static ProTvUpdateInfo Remember(ProTvUpdateInfo result)
    {
        lock (Gate)
        {
            _cached = result;
            _cachedUtc = DateTime.UtcNow;
        }

        return result;
    }
}

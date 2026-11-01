using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace PROTV.Plugin;

// Whether PROTV really has a stream behind a channel, as of the last check.
public enum ProTvChannelState
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

// One channel's answer: the state, when it was taken, and the reason when it is offline.
public sealed class ProTvChannelHealthResult
{
    public ProTvChannelState State { get; init; }

    public DateTime CheckedUtc { get; init; }

    public string? Message { get; init; }
}

// What the dashboard and the API report about the last check.
public sealed class ProTvChannelHealthReport
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("checked")]
    public int Checked { get; set; }

    [JsonPropertyName("online")]
    public int Online { get; set; }

    [JsonPropertyName("offline")]
    public int Offline { get; set; }

    [JsonPropertyName("lastRunUtc")]
    public DateTime? LastRunUtc { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

// Probes the channels PROTV serves and remembers what came back, so the home page tab can grey out a
// channel that has no stream instead of letting it fail when somebody plays it.
//
// A channel counts as online only when PROTV hands out a stream address for it *and* that address
// produces bytes within a few seconds. A multicast address is joined exactly the way playback joins
// it, on its own socket, so the answer matches what a viewer would get and a probe never disturbs a
// viewer who is already watching. An HTTP address is opened and its first bytes are read.
//
// Nothing here writes to the settings file or to Jellyfin's database: the result lives in memory
// until the next check (and is thrown away when Jellyfin restarts).
public static class ProTvChannelHealth
{
    // Channels are probed a few at a time: each one waits for data, so more parallelism would only
    // add sockets without finishing sooner.
    private const int MaxParallelProbes = 5;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly ConcurrentDictionary<int, ProTvChannelHealthResult> Results = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient ProbeClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static volatile bool _running;
    private static DateTime _lastRunUtc;
    private static string? _lastError;

    public static bool IsRunning => _running;

    public static DateTime LastRunUtc => _lastRunUtc;

    public static string? LastError => _lastError;

    public static ProTvChannelHealthResult? Get(int channelId)
        => Results.TryGetValue(channelId, out var result) ? result : null;

    // What the home page tab draws next to a channel: "online", "offline" or "unknown" (never
    // checked, or checked before this Jellyfin started).
    public static string Describe(int channelId)
    {
        var result = Get(channelId);
        return result?.State switch
        {
            ProTvChannelState.Online => "online",
            ProTvChannelState.Offline => "offline",
            _ => "unknown"
        };
    }

    public static ProTvChannelHealthReport GetStatus()
    {
        var report = new ProTvChannelHealthReport
        {
            Running = _running,
            Checked = Results.Count,
            LastRunUtc = _lastRunUtc == default ? null : _lastRunUtc,
            LastError = _lastError
        };

        foreach (var result in Results.Values)
        {
            if (result.State == ProTvChannelState.Online)
            {
                report.Online++;
            }
            else if (result.State == ProTvChannelState.Offline)
            {
                report.Offline++;
            }
        }

        return report;
    }

    // Checks every channel PROTV serves. A second caller gets the report of the run that is already
    // going instead of starting another one.
    public static async Task<ProTvChannelHealthReport> RunAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Already running: the caller watches that run through the status instead.
            return GetStatus();
        }

        try
        {
            _running = true;
            _lastError = null;

            var api = ProTvSharedClient.Get();
            var channels = ProTvChannelOrdering.Sort(await api.GetChannelsAsync(cancellationToken).ConfigureAwait(false));

            // Channels that PROTV no longer serves would otherwise keep an old answer for ever.
            foreach (var stale in Results.Keys.Where(id => !channels.Any(channel => channel.Id == id)).ToList())
            {
                Results.TryRemove(stale, out _);
            }

            var done = 0;
            await Parallel.ForEachAsync(
                channels,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProbes, CancellationToken = cancellationToken },
                async (channel, token) =>
                {
                    var result = await ProbeAsync(api, channel.Id, token).ConfigureAwait(false);
                    Results[channel.Id] = result;
                    var finished = Interlocked.Increment(ref done);
                    progress?.Report(channels.Count == 0 ? 100 : finished * 100.0 / channels.Count);
                }).ConfigureAwait(false);

            _lastRunUtc = DateTime.UtcNow;
            return GetStatus();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastError = exception.Message;
            return GetStatus();
        }
        finally
        {
            _running = false;
            Gate.Release();
        }
    }

    private static async Task<ProTvChannelHealthResult> ProbeAsync(
        ProTvApiClient api,
        int channelId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var url = await api.WatchChannelAsync(channelId, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                return Offline("PROTV did not return a stream address for this channel.");
            }

            return await ProducesDataAsync(url, timeout.Token).ConfigureAwait(false)
                ? Online()
                : Offline($"PROTV returned {Scheme(url)} but no data arrived within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Offline($"No answer from PROTV within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception)
        {
            return Offline(exception.Message);
        }
    }

    // True when the stream address actually delivers something. The multicast address is joined the
    // same way the tuner joins it, so a router that does not forward the group shows up here too.
    private static async Task<bool> ProducesDataAsync(string url, CancellationToken cancellationToken)
    {
        if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await MulticastStream.OpenAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var buffer = new byte[65536];
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        using var response = await ProbeClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var head = new byte[1024];
        return await content.ReadAsync(head, cancellationToken).ConfigureAwait(false) > 0;
    }

    // Only the scheme is ever reported: a stream address can carry a token.
    private static string Scheme(string url)
    {
        var separator = url.IndexOf(':');
        return separator > 0 ? url[..separator].ToLowerInvariant() + "://" : "an address";
    }

    private static ProTvChannelHealthResult Online() => new()
    {
        State = ProTvChannelState.Online,
        CheckedUtc = DateTime.UtcNow
    };

    private static ProTvChannelHealthResult Offline(string message) => new()
    {
        State = ProTvChannelState.Offline,
        CheckedUtc = DateTime.UtcNow,
        Message = message
    };
}

// The background job behind the check. Jellyfin discovers plugin scheduled tasks on its own, so it
// shows up in Dashboard > Scheduled Tasks and can be run from there or from the PROTV dashboard page.
public sealed class ProTvChannelHealthTask : MediaBrowser.Model.Tasks.IScheduledTask
{
    public string Name => "Check PROTV channel health";

    public string Key => "ProTvChannelHealth";

    public string Description =>
        "Asks PROTV for every channel's stream address and checks that data actually arrives. " +
        "Channels that answer nothing are marked in the PROTV home page tab so nobody waits for a " +
        "channel that cannot play.";

    public string Category => "PROTV";

    public IEnumerable<MediaBrowser.Model.Tasks.TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new MediaBrowser.Model.Tasks.TaskTriggerInfo
        {
            Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        }
    };

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => ProTvChannelHealth.RunAsync(progress, cancellationToken);
}

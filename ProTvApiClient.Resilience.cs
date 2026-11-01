using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PROTV.Plugin;

// The "PROTV is flaky" half of the API client: retries for reads, a circuit breaker, and the
// service status the dashboard and the home tab show. Settings: PluginConfiguration.LiveApiResilience.
public sealed partial class ProTvApiClient
{
    // Three tries in all (two retries). Reads only: see SendAsync.
    private const int MaxReadAttempts = 3;

    // One try may take this long before it counts as a timeout. HttpClient's own limit is 100 s, which
    // is far too long to wait before the service can be called unavailable.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    private readonly ProTvCircuitBreaker _breaker = new(() => DateTime.UtcNow);
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    // Retries (reads only) and the breaker sit in front of every call to PROTV, including the VOD
    // ones, because they all go through here.
    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        bool addDeviceHeaders,
        string? acceptLanguage,
        CancellationToken cancellationToken)
    {
        if (!Configuration.LiveApiResilience)
        {
            return await SendOnceAsync<T>(method, path, addDeviceHeaders, acceptLanguage, cancellationToken);
        }

        // A POST (the Watch call) gets one attempt: PROTV may count it, and only reads are safe to repeat.
        var attempts = method == HttpMethod.Get ? MaxReadAttempts : 1;
        return await SendResilientAsync<T>(method, path, addDeviceHeaders, acceptLanguage, attempts, cancellationToken);
    }

    private async Task<T> SendResilientAsync<T>(
        HttpMethod method,
        string path,
        bool addDeviceHeaders,
        string? acceptLanguage,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Throws without contacting PROTV while requests are paused.
            _breaker.ThrowIfOpen();
            var reported = false;
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(AttemptTimeout);
                var result = await SendOnceAsync<T>(method, path, addDeviceHeaders, acceptLanguage, attemptTimeout.Token);
                reported = true;
                _breaker.RecordSuccess();
                return result;
            }
            catch (Exception exception) when (ProTvRetryPolicy.IsTransient(exception, cancellationToken))
            {
                reported = true;
                var paused = _breaker.RecordFailure();
                if (attempt >= maxAttempts || paused)
                {
                    throw;
                }

                _logger.LogInformation("PROTV did not answer (try {Attempt} of {MaxAttempts}); trying again.", attempt, maxAttempts);
                await Task.Delay(ProTvRetryPolicy.Backoff(attempt, Random.Shared.NextDouble()), cancellationToken);
            }
            catch (HttpRequestException exception) when (ProTvRetryPolicy.IsRefusal(exception))
            {
                // PROTV answered and said no (a rejected login, for one). It is reachable, so this is
                // not an outage, and it is never repeated: retrying a rejected login risks a lockout.
                reported = true;
                _breaker.RecordSuccess();
                throw;
            }
            finally
            {
                if (!reported)
                {
                    // Cancelled by the caller, or a settings problem (no password saved): no verdict on PROTV.
                    _breaker.ReleaseTrial();
                }
            }
        }
    }

    // What /PROTV/ServiceStatus and the diagnostic report show. allowProbe lets the dashboard and the
    // home tab, which poll it, also bring a stale "not ok" back to "ok" once PROTV answers again.
    public ProTvServiceStatus GetServiceStatus(bool allowProbe = true)
    {
        if (!Configuration.LiveApiResilience)
        {
            return ProTvServiceStatus.MonitoringOff(DateTime.UtcNow);
        }

        if (allowProbe && _breaker.ShouldProbe())
        {
            StartProbe();
        }

        return _breaker.Snapshot();
    }

    // With no other traffic nothing would ever tell the breaker that PROTV is back. One light request
    // (at most every 15 s, and only while the state is not ok) does, off the caller's thread so the
    // status endpoint answers at once.
    private void StartProbe()
    {
        if (!_probeGate.Wait(0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await SendResilientAsync<ProTvListResponse<ProTvChannel>>(
                    HttpMethod.Get,
                    "api/Channels?Page=1&Count=1&Order=Desc",
                    addDeviceHeaders: false,
                    acceptLanguage: null,
                    maxAttempts: 1,
                    timeout.Token);
            }
            catch (Exception)
            {
                // The breaker has recorded the outcome; there is nobody to report it to.
            }
            finally
            {
                _probeGate.Release();
            }
        });
    }
}

// What the plugin knows about PROTV's health: "ok", "degraded" (failures, still trying) or
// "unavailable" (requests paused). The message is fixed text on purpose: an exception message could
// carry an address or a token, and this goes to every signed-in user.
public sealed class ProTvServiceStatus
{
    [JsonPropertyName("state")]
    public string State { get; init; } = ProTvServiceState.Ok;

    [JsonPropertyName("sinceUtc")]
    public string SinceUtc => Since.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonIgnore]
    public DateTime Since { get; init; }

    internal static ProTvServiceStatus For(string state, DateTime sinceUtc) => new()
    {
        State = state,
        Since = sinceUtc,
        Message = state switch
        {
            ProTvServiceState.Unavailable => "PROTV is not answering. Requests are paused for a short while.",
            ProTvServiceState.Degraded => "PROTV is slow or not answering. Trying again.",
            _ => "PROTV is answering."
        }
    };

    internal static ProTvServiceStatus MonitoringOff(DateTime nowUtc) => new()
    {
        State = ProTvServiceState.Ok,
        Since = nowUtc,
        Message = "Service monitoring is switched off."
    };
}

public static class ProTvServiceState
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}

// Thrown without contacting PROTV while requests are paused. It derives from HttpRequestException so
// code that already treats "PROTV not reachable" as a network failure keeps working. The text has no
// digits on purpose: the Watch fallback looks for "404" / "405" in these messages.
internal sealed class ProTvServiceUnavailableException : HttpRequestException
{
    public ProTvServiceUnavailableException()
        : base("PROTV is temporarily unavailable; requests to it are paused for a short while.")
    {
    }
}

// The decisions behind the retries, kept free of any I/O so they can be unit-tested.
internal static class ProTvRetryPolicy
{
    private const double BaseDelayMilliseconds = 500;

    // 0.5 s after the first failed try, 1 s after the second, each stretched or shrunk by up to a
    // quarter so many callers that failed together do not retry together. randomUnit is in [0, 1).
    public static TimeSpan Backoff(int failedAttempts, double randomUnit)
    {
        var exponential = BaseDelayMilliseconds * Math.Pow(2, failedAttempts - 1);
        return TimeSpan.FromMilliseconds(exponential * (0.75 + (0.5 * randomUnit)));
    }

    // Worth another try: a timeout, a connection problem or an HTTP 5xx. Never when the caller
    // cancelled, so a cancelled request stops at once.
    public static bool IsTransient(Exception exception, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            // The caller did not cancel, so this is the attempt timeout or HttpClient's own.
            OperationCanceledException => true,
            ProTvServiceUnavailableException => false,
            HttpRequestException http => http.StatusCode is null
                || (int)http.StatusCode >= 500
                || http.StatusCode == HttpStatusCode.RequestTimeout,
            IOException => true,
            SocketException => true,
            _ => false
        };
    }

    // PROTV answered with a 4xx (not 408): the request itself was refused.
    public static bool IsRefusal(HttpRequestException exception)
        => exception.StatusCode is { } code
            && (int)code is >= 400 and < 500
            && code != HttpStatusCode.RequestTimeout;
}

// Five failed tries in a row pause requests for a minute (they fail at once instead of piling up on a
// service that is not answering); after that one request is let through to test it, and its result
// decides. A success at any time puts everything back to normal. The clock is injected for tests.
internal sealed class ProTvCircuitBreaker
{
    public const int FailureThreshold = 5;
    public static readonly TimeSpan PauseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private int _consecutiveFailures;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _trialInFlight;
    private string _state = ProTvServiceState.Ok;
    private DateTime _sinceUtc;

    public ProTvCircuitBreaker(Func<DateTime> clock)
    {
        _clock = clock;
        _sinceUtc = clock();
    }

    // Called before every try. Lets it through, or throws while PROTV is paused.
    public void ThrowIfOpen()
    {
        lock (_gate)
        {
            var now = _clock();
            if (now < _pausedUntilUtc)
            {
                throw new ProTvServiceUnavailableException();
            }

            if (_consecutiveFailures >= FailureThreshold)
            {
                // The pause is over: one request tests PROTV, the others keep failing fast until it reports.
                if (_trialInFlight)
                {
                    throw new ProTvServiceUnavailableException();
                }

                _trialInFlight = true;
            }

            _lastAttemptUtc = now;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _trialInFlight = false;
            Evaluate(_clock());
        }
    }

    // True when requests are now paused.
    public bool RecordFailure()
    {
        lock (_gate)
        {
            var now = _clock();
            _trialInFlight = false;
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold)
            {
                _pausedUntilUtc = now + PauseDuration;
            }

            Evaluate(now);
            return now < _pausedUntilUtc;
        }
    }

    // A try that says nothing about PROTV (cancelled, no password saved) must not keep the test slot.
    public void ReleaseTrial()
    {
        lock (_gate)
        {
            _trialInFlight = false;
        }
    }

    public bool ShouldProbe()
    {
        lock (_gate)
        {
            var now = _clock();
            Evaluate(now);
            return _state != ProTvServiceState.Ok
                && now >= _pausedUntilUtc
                && !_trialInFlight
                && now - _lastAttemptUtc >= ProbeInterval;
        }
    }

    public ProTvServiceStatus Snapshot()
    {
        lock (_gate)
        {
            Evaluate(_clock());
            return ProTvServiceStatus.For(_state, _sinceUtc);
        }
    }

    // The state follows from the counters and the clock, so it is worked out when asked: a pause that
    // ran out becomes "degraded" at the moment it ended, even if nobody looked in between.
    private void Evaluate(DateTime now)
    {
        var next = now < _pausedUntilUtc
            ? ProTvServiceState.Unavailable
            : _consecutiveFailures > 0 ? ProTvServiceState.Degraded : ProTvServiceState.Ok;
        if (next == _state)
        {
            return;
        }

        _sinceUtc = _state == ProTvServiceState.Unavailable && next == ProTvServiceState.Degraded
            ? _pausedUntilUtc
            : now;
        _state = next;
    }
}

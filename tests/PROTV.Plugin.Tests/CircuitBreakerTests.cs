using System.Net;

namespace PROTV.Plugin.Tests;

public sealed class CircuitBreakerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = Start;

    private ProTvCircuitBreaker NewBreaker() => new(() => _now);

    private static void Fail(ProTvCircuitBreaker breaker, int times)
    {
        for (var index = 0; index < times; index++)
        {
            breaker.ThrowIfOpen();
            breaker.RecordFailure();
        }
    }

    [Fact]
    public void Starts_ok()
    {
        Assert.Equal(ProTvServiceState.Ok, NewBreaker().Snapshot().State);
    }

    [Fact]
    public void Four_failures_are_degraded_and_the_fifth_pauses_requests()
    {
        var breaker = NewBreaker();
        Fail(breaker, 4);
        Assert.Equal(ProTvServiceState.Degraded, breaker.Snapshot().State);

        breaker.ThrowIfOpen();
        Assert.True(breaker.RecordFailure());
        Assert.Equal(ProTvServiceState.Unavailable, breaker.Snapshot().State);
        Assert.Throws<ProTvServiceUnavailableException>(breaker.ThrowIfOpen);
    }

    [Fact]
    public void A_success_before_the_threshold_resets_the_count()
    {
        var breaker = NewBreaker();
        Fail(breaker, 4);
        breaker.ThrowIfOpen();
        breaker.RecordSuccess();
        Assert.Equal(ProTvServiceState.Ok, breaker.Snapshot().State);

        Fail(breaker, 4);
        Assert.Equal(ProTvServiceState.Degraded, breaker.Snapshot().State);
    }

    [Fact]
    public void After_the_pause_one_request_tests_the_service_and_success_closes_it()
    {
        var breaker = NewBreaker();
        Fail(breaker, ProTvCircuitBreaker.FailureThreshold);
        _now += ProTvCircuitBreaker.PauseDuration;

        breaker.ThrowIfOpen();
        Assert.Throws<ProTvServiceUnavailableException>(breaker.ThrowIfOpen);

        breaker.RecordSuccess();
        Assert.Equal(ProTvServiceState.Ok, breaker.Snapshot().State);
        breaker.ThrowIfOpen();
    }

    [Fact]
    public void A_failed_test_pauses_again()
    {
        var breaker = NewBreaker();
        Fail(breaker, ProTvCircuitBreaker.FailureThreshold);
        _now += ProTvCircuitBreaker.PauseDuration;

        breaker.ThrowIfOpen();
        Assert.True(breaker.RecordFailure());
        Assert.Equal(ProTvServiceState.Unavailable, breaker.Snapshot().State);
        Assert.Throws<ProTvServiceUnavailableException>(breaker.ThrowIfOpen);
    }

    [Fact]
    public void A_released_test_slot_can_be_used_again()
    {
        var breaker = NewBreaker();
        Fail(breaker, ProTvCircuitBreaker.FailureThreshold);
        _now += ProTvCircuitBreaker.PauseDuration;

        breaker.ThrowIfOpen();
        breaker.ReleaseTrial();
        breaker.ThrowIfOpen();
    }

    [Fact]
    public void Degraded_since_is_when_the_pause_ended()
    {
        var breaker = NewBreaker();
        Fail(breaker, ProTvCircuitBreaker.FailureThreshold);
        var pauseEnds = _now + ProTvCircuitBreaker.PauseDuration;

        _now = pauseEnds + TimeSpan.FromSeconds(7);
        var status = breaker.Snapshot();

        Assert.Equal(ProTvServiceState.Degraded, status.State);
        Assert.Equal(pauseEnds, status.Since);
    }

    [Fact]
    public void Probing_waits_for_the_pause_and_the_interval_and_a_free_test_slot()
    {
        var breaker = NewBreaker();
        Assert.False(breaker.ShouldProbe());

        Fail(breaker, ProTvCircuitBreaker.FailureThreshold);
        Assert.False(breaker.ShouldProbe());

        _now += ProTvCircuitBreaker.PauseDuration + TimeSpan.FromSeconds(20);
        Assert.True(breaker.ShouldProbe());

        breaker.ThrowIfOpen();
        Assert.False(breaker.ShouldProbe());
    }

    [Theory]
    [InlineData(1, 0.0, 375)]
    [InlineData(1, 0.5, 500)]
    [InlineData(2, 0.5, 1000)]
    [InlineData(3, 1.0, 2500)]
    public void Backoff_doubles_with_a_quarter_of_jitter(int failedAttempts, double randomUnit, double expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, ProTvRetryPolicy.Backoff(failedAttempts, randomUnit).TotalMilliseconds, 3);
    }

    [Fact]
    public void Timeouts_connection_problems_and_server_errors_are_worth_another_try()
    {
        var none = CancellationToken.None;
        Assert.True(ProTvRetryPolicy.IsTransient(new TaskCanceledException(), none));
        Assert.True(ProTvRetryPolicy.IsTransient(new HttpRequestException("connection refused"), none));
        Assert.True(ProTvRetryPolicy.IsTransient(new HttpRequestException("x", null, HttpStatusCode.InternalServerError), none));
        Assert.True(ProTvRetryPolicy.IsTransient(new HttpRequestException("x", null, HttpStatusCode.RequestTimeout), none));
        Assert.True(ProTvRetryPolicy.IsTransient(new IOException("reset"), none));
    }

    [Fact]
    public void A_refused_request_is_never_retried()
    {
        var none = CancellationToken.None;
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            var exception = new HttpRequestException("x", null, code);
            Assert.False(ProTvRetryPolicy.IsTransient(exception, none));
            Assert.True(ProTvRetryPolicy.IsRefusal(exception));
        }

        Assert.False(ProTvRetryPolicy.IsRefusal(new HttpRequestException("x", null, HttpStatusCode.RequestTimeout)));
        Assert.False(ProTvRetryPolicy.IsRefusal(new HttpRequestException("x", null, HttpStatusCode.InternalServerError)));
    }

    [Fact]
    public void A_cancelled_caller_or_a_paused_service_is_not_retried()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(ProTvRetryPolicy.IsTransient(new TaskCanceledException(), cancelled.Token));
        Assert.False(ProTvRetryPolicy.IsTransient(new ProTvServiceUnavailableException(), CancellationToken.None));
    }
}

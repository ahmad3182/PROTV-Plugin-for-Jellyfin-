using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PROTV.Plugin;

public sealed class ProTvParentalPinRequest
{
    // Empty = clear the PIN.
    public string? Pin { get; set; }
}

public sealed class ProTvParentalUnlockRequest
{
    public string? Pin { get; set; }
}

public sealed class ProTvParentalUnlockResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("expiresUtc")]
    public DateTime ExpiresUtc { get; set; }
}

public sealed partial class ProTvTabController
{
    private const int MaxUnlockFailures = 5;
    private static readonly TimeSpan UnlockLockout = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<Guid, UnlockAttempt> UnlockAttempts = new();

    private sealed class UnlockAttempt
    {
        public int Failures { get; set; }

        public DateTime LockedUntilUtc { get; set; }
    }

    // Admin: whether a PIN exists (the hash itself never leaves the server).
    [HttpGet("Parental/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvParentalStatus> GetParentalStatus()
        => Ok(ProTvParental.GetStatus());

    // Admin: set or clear the PIN. Channel locks are saved with the normal settings form.
    [HttpPost("Parental/Pin")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvParentalStatus> SetParentalPin([FromBody] ProTvParentalPinRequest request)
    {
        if (!ProTvParental.IsAcceptablePin(request?.Pin))
        {
            return BadRequest(new { message = "The PIN must be 4 to 12 characters." });
        }

        ProTvParental.SetPin(request?.Pin);
        return Ok(ProTvParental.GetStatus());
    }

    // Any signed-in user: exchange the PIN for a short-lived token bound to that user.
    [HttpPost("Parental/Unlock")]
    [Authorize]
    public ActionResult<ProTvParentalUnlockResponse> UnlockParental([FromBody] ProTvParentalUnlockRequest request)
    {
        if (!ProTvParental.Enabled)
        {
            return BadRequest(new { message = "Parental controls are off." });
        }

        if (!ProTvParental.GetStatus().PinSet)
        {
            return BadRequest(new { message = "No parental PIN is configured." });
        }

        var userId = ProTvParental.GetUserId(User);
        if (userId == Guid.Empty)
        {
            return BadRequest(new { message = "Could not identify the Jellyfin user." });
        }

        var attempt = UnlockAttempts.GetOrAdd(userId, _ => new UnlockAttempt());
        lock (attempt)
        {
            var now = DateTime.UtcNow;
            if (attempt.LockedUntilUtc > now)
            {
                var seconds = Math.Max(1, (int)(attempt.LockedUntilUtc - now).TotalSeconds);
                Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return StatusCode(429, new { message = "Too many incorrect PIN attempts. Try again later." });
            }

            if (!ProTvParental.VerifyPin(request?.Pin))
            {
                attempt.Failures++;
                if (attempt.Failures >= MaxUnlockFailures)
                {
                    attempt.Failures = 0;
                    attempt.LockedUntilUtc = now.Add(UnlockLockout);
                }

                return BadRequest(new { message = "Incorrect PIN." });
            }

            UnlockAttempts.TryRemove(userId, out _);
        }

        var token = ProTvParental.CreateUnlockToken(userId);
        if (token is null)
        {
            return BadRequest(new { message = "Could not create an unlock token." });
        }

        return Ok(new ProTvParentalUnlockResponse
        {
            Token = token,
            ExpiresUtc = DateTime.UtcNow.Add(ProTvParental.UnlockLifetime)
        });
    }
}

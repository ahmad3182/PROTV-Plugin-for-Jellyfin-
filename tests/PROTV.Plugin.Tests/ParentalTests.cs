using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PROTV.Plugin.Tests;

public sealed class ParentalTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "protv-parental-tests-" + Guid.NewGuid().ToString("N"));

    public ParentalTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        ProTvSecretStore.ConfigureForTests(_dataDirectory);
        ProTvParental.SetPin(null);
    }

    public void Dispose()
    {
        ProTvParental.SetPin(null);
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // A failed cleanup must not hide the test result.
        }
    }

    [Fact]
    public void Pin_is_stored_as_a_salted_hash_and_verified_in_constant_time()
    {
        ProTvParental.SetPin("2468");

        Assert.True(ProTvParental.GetStatus().PinSet);
        Assert.True(ProTvParental.VerifyPin("2468"));
        Assert.False(ProTvParental.VerifyPin("2469"));
        Assert.False(ProTvParental.VerifyPin(string.Empty));
        Assert.False(ProTvParental.VerifyPin(null));

        var file = File.ReadAllText(ProTvParental.FilePath());
        Assert.DoesNotContain("2468", file);
        using var document = JsonDocument.Parse(file);
        Assert.True(document.RootElement.GetProperty("Iterations").GetInt32() >= 100_000);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Salt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Hash").GetString()));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456789012", true)]
    [InlineData(" 1234 ", true)]
    [InlineData("123", false)]
    [InlineData("1234567890123", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Pin_length_is_validated(string? pin, bool expected)
    {
        Assert.Equal(expected, ProTvParental.IsAcceptablePin(pin));
    }

    [Fact]
    public void Unlock_token_is_bound_to_the_user_and_rejects_tampering()
    {
        ProTvParental.SetPin("2468");
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();

        var token = ProTvParental.CreateUnlockToken(user);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(ProTvParental.ValidateUnlockToken(token, user));
        Assert.False(ProTvParental.ValidateUnlockToken(token, other));
        Assert.False(ProTvParental.ValidateUnlockToken(null, user));
        Assert.False(ProTvParental.ValidateUnlockToken("not-a-token", user));
        Assert.False(ProTvParental.ValidateUnlockToken(token + "x", user));

        // Flip a byte in the signature, not just the last base64 character (whose unused bits can
        // decode to the same signature).
        var parts = token!.Split('.');
        var signature = parts[1].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        var tampered = parts[0] + "." + new string(signature);
        Assert.False(ProTvParental.ValidateUnlockToken(tampered, user));
    }

    [Fact]
    public void Expired_unlock_token_is_rejected()
    {
        ProTvParental.SetPin("2468");
        var user = Guid.NewGuid();
        var file = JsonDocument.Parse(File.ReadAllText(ProTvParental.FilePath()));
        var secret = Convert.FromBase64String(file.RootElement.GetProperty("TokenSecret").GetString()!);

        var payload = user.ToString("N") + "|" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        var signature = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload));
        var token = Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(signature);

        Assert.False(ProTvParental.ValidateUnlockToken(token, user));
    }

    [Fact]
    public void Clearing_the_pin_removes_the_file_and_disables_unlocks()
    {
        ProTvParental.SetPin("2468");
        Assert.True(File.Exists(ProTvParental.FilePath()));

        ProTvParental.SetPin("   ");

        Assert.False(ProTvParental.GetStatus().PinSet);
        Assert.False(File.Exists(ProTvParental.FilePath()));
        Assert.Null(ProTvParental.CreateUnlockToken(Guid.NewGuid()));
        Assert.False(ProTvParental.VerifyPin("2468"));
    }

    [Fact]
    public void User_id_comes_from_the_jellyfin_claim()
    {
        var user = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Jellyfin-UserId", user.ToString("D"))
        }, "test"));

        Assert.Equal(user, ProTvParental.GetUserId(principal));
        Assert.Equal(Guid.Empty, ProTvParental.GetUserId(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(Guid.Empty, ProTvParental.GetUserId(null));
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

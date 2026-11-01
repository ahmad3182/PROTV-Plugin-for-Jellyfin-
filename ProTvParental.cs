using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace PROTV.Plugin;

// The dashboard asks for this to show whether a PIN has been configured. The hash and the
// token-signing secret never leave the server.
public sealed class ProTvParentalStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("pinSet")]
    public bool PinSet { get; set; }
}

internal sealed class ProTvParentalFile
{
    public string Salt { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public int Iterations { get; set; }

    public string TokenSecret { get; set; } = string.Empty;
}

// Channel-level parental controls for the home page tab.
//
// The PIN itself is never stored: only a PBKDF2-SHA256 hash (150k iterations) in
// <data>/protv/parental.json. Unlocking returns a short-lived HMAC token bound to the Jellyfin
// user id, which the tab sends back with X-PROTV-Unlock. This hides locked channels and their
// EPG on the tab; Jellyfin's own per-user Live TV permissions remain the real access control.
internal static class ProTvParental
{
    private const int Iterations = 150_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int TokenSecretSizeBytes = 32;
    private const string UnlockHeader = "X-PROTV-Unlock";
    internal static TimeSpan UnlockLifetime => TimeSpan.FromHours(12);
    private static readonly object Gate = new();

    public static bool Enabled => Plugin.Instance?.Configuration.ParentalEnabled == true;

    public static bool IsChannelLocked(int channelId)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.ParentalEnabled != true)
        {
            return false;
        }

        var overrides = configuration.ChannelOverrides ?? Array.Empty<ProTvChannelOverride>();
        return overrides.LastOrDefault(item => item.ChannelId == channelId)?.ParentalLocked == true;
    }

    public static ProTvParentalStatus GetStatus()
        => new()
        {
            Enabled = Enabled,
            PinSet = Load(out _) is not null
        };

    // An empty value clears the PIN. A real PIN must be 4-12 characters, so a mistyped single
    // character cannot silently become the code that protects a child's channels.
    internal static bool IsAcceptablePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return true;
        }

        var length = pin.Trim().Length;
        return length is >= 4 and <= 12;
    }

    // Empty/null clears the PIN. A non-empty value replaces it, keeping the token secret so
    // existing unlocks are not invalidated unnecessarily.
    public static void SetPin(string? pin)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                TryDelete();
                return;
            }

            var existing = LoadUnlocked();
            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                pin.Trim(),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashSizeBytes);
            var tokenSecret = existing?.TokenSecret;
            if (string.IsNullOrWhiteSpace(tokenSecret))
            {
                tokenSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenSecretSizeBytes));
            }

            Save(new ProTvParentalFile
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                Iterations = Iterations,
                TokenSecret = tokenSecret
            });
        }
    }

    public static bool VerifyPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return false;
        }

        var file = Load();
        if (file is null)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(file.Salt);
            var expected = Convert.FromBase64String(file.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                pin.Trim(),
                salt,
                Math.Max(1, file.Iterations),
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    public static string? CreateUnlockToken(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var file = Load();
        if (file is null || string.IsNullOrWhiteSpace(file.TokenSecret))
        {
            return null;
        }

        try
        {
            var secret = Convert.FromBase64String(file.TokenSecret);
            var payload = string.Join(
                "|",
                userId.ToString("N"),
                DateTimeOffset.UtcNow.Add(UnlockLifetime).ToUnixTimeSeconds().ToString());
            var signature = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload));
            return Base64Url(Encoding.UTF8.GetBytes(payload)) + "." + Base64Url(signature);
        }
        catch
        {
            return null;
        }
    }

    // True when the request belongs to a user who has unlocked the locked channels, or when
    // parental controls are switched off. Failure is always closed (locked) otherwise, including
    // when the PIN file is missing or damaged: locked channels then stay hidden until an admin
    // saves a new PIN instead of silently becoming public.
    public static bool IsUnlocked(HttpRequest request)
    {
        if (!Enabled)
        {
            return true;
        }

        if (Load(out _) is null)
        {
            return false;
        }

        var userId = GetUserId(request.HttpContext.User);
        if (userId == Guid.Empty)
        {
            return false;
        }

        // Header only: the token must not travel in URLs where proxies and browser history can
        // capture it. The tab uses ApiClient.ajax for requests made after an unlock.
        var token = request.Headers[UnlockHeader].FirstOrDefault();
        return ValidateUnlockToken(token, userId);
    }

    // Extracted so the signature, expiry and per-user binding can be tested without constructing
    // an ASP.NET request. Failure is always closed.
    internal static bool ValidateUnlockToken(string? token, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(token) || userId == Guid.Empty)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var file = Load();
            if (file is null || string.IsNullOrWhiteSpace(file.TokenSecret))
            {
                return false;
            }

            var secret = Convert.FromBase64String(file.TokenSecret);
            var payload = Encoding.UTF8.GetString(FromBase64Url(parts[0]));
            var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload));
            var actual = FromBase64Url(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return false;
            }

            var fields = payload.Split('|');
            if (fields.Length != 2
                || !Guid.TryParseExact(fields[0], "N", out var tokenUser)
                || tokenUser != userId
                || !long.TryParse(fields[1], out var expires))
            {
                return false;
            }

            return expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch
        {
            return false;
        }
    }

    internal static Guid GetUserId(ClaimsPrincipal? user)
    {
        var claim = user?.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }

    internal static string FilePath()
        => Path.Combine(ProTvSecretStore.StorageFolder(), "parental.json");

    private static ProTvParentalFile? Load()
    {
        lock (Gate)
        {
            return LoadUnlocked();
        }
    }

    private static ProTvParentalFile? Load(out bool unreadable)
    {
        lock (Gate)
        {
            unreadable = false;
            try
            {
                var path = FilePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                var file = JsonSerializer.Deserialize<ProTvParentalFile>(File.ReadAllText(path));
                if (file is null)
                {
                    unreadable = true;
                }

                return file;
            }
            catch
            {
                unreadable = true;
                return null;
            }
        }
    }

    private static ProTvParentalFile? LoadUnlocked()
    {
        try
        {
            var path = FilePath();
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProTvParentalFile>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void Save(ProTvParentalFile file)
    {
        var path = FilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Clearing the PIN is best-effort; an unreadable file still counts as "PIN set".
        }
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        switch (text.Length % 4)
        {
            case 2:
                text += "==";
                break;
            case 3:
                text += "=";
                break;
        }

        return Convert.FromBase64String(text);
    }
}

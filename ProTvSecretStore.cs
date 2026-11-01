using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;

namespace PROTV.Plugin;

// Encrypts the PROTV password before Jellyfin writes it to PROTV.Plugin.xml.
//
// The key is a random 32-byte file kept in <data>/protv/secret.key, outside the plugin
// configuration folder, so copying or screenshotting PROTV.Plugin.xml (or a Jellyfin config
// backup that only covers plugins/configurations) does not reveal the password. It is not a
// defence against someone who can read the whole Jellyfin data folder; the goal is to keep the
// password out of casual leaks and plain-text backups.
internal static class ProTvSecretStore
{
    private const string Prefix = "enc:v1:";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const string KeyFileName = "secret.key";
    private const string KeyFolderName = "protv";

    private static readonly object Gate = new();
    private static byte[]? _key;
    private static string? _dataDirectory;
    private static string? _lastError;

    public static string? LastError
    {
        get
        {
            lock (Gate)
            {
                return _lastError;
            }
        }
    }

    public static bool IsConfigured
    {
        get
        {
            lock (Gate)
            {
                return _key is not null;
            }
        }
    }

    // Called from Plugin's constructor. Without it the store falls back to deriving the data
    // folder from the plugin assembly location, which is what the service registrator needs
    // before Jellyfin has created the plugin instance.
    public static void Configure(IApplicationPaths? applicationPaths)
    {
        var dataPath = applicationPaths?.DataPath;
        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            lock (Gate)
            {
                if (!string.Equals(_dataDirectory, dataPath, StringComparison.OrdinalIgnoreCase))
                {
                    _dataDirectory = dataPath;
                    _key = null;
                }
            }
        }
    }

    // Test seam: use an isolated folder instead of the Jellyfin data folder.
    internal static void ConfigureForTests(string dataDirectory)
    {
        lock (Gate)
        {
            _dataDirectory = dataDirectory;
            _key = null;
            _lastError = null;
        }
    }

    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    // True when the value is a legacy plain-text password that should be rewritten encrypted.
    public static bool NeedsProtection(string? value)
        => !string.IsNullOrEmpty(value) && !IsProtected(value);

    // Returns the encrypted envelope. A value that is already encrypted is returned unchanged, so
    // reading and re-saving the configuration can never double-encrypt it. Empty values stay
    // empty, and a missing/unreadable key falls back to the input instead of losing the password.
    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value))
        {
            return value ?? string.Empty;
        }

        var key = GetKey();
        if (key is null)
        {
            return value;
        }

        try
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSizeBytes];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Encrypt(nonce, plain, cipher, tag);

            var packed = new byte[NonceSizeBytes + TagSizeBytes + cipher.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, NonceSizeBytes);
            Buffer.BlockCopy(tag, 0, packed, NonceSizeBytes, TagSizeBytes);
            Buffer.BlockCopy(cipher, 0, packed, NonceSizeBytes + TagSizeBytes, cipher.Length);
            return Prefix + Convert.ToBase64String(packed);
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
            return value;
        }
    }

    // True when a stored secret is either absent or is an encrypted envelope this server can read.
    // This deliberately loads the key, so a dashboard status call is not fooled by an envelope that
    // has not been decrypted yet (IsConfigured alone only says whether the key was already loaded).
    public static bool CanDecrypt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return IsProtected(value) && Unprotect(value).Length > 0;
    }

    // Returns the plain-text password. A legacy plain-text value is returned as-is so an upgrade
    // keeps working. An encrypted value that cannot be decrypted returns an empty string rather
    // than sending the ciphertext to PROTV as if it were the password.
    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsProtected(value))
        {
            return value ?? string.Empty;
        }

        var key = GetKey();
        if (key is null)
        {
            return string.Empty;
        }

        try
        {
            var packed = Convert.FromBase64String(value[Prefix.Length..]);
            if (packed.Length < NonceSizeBytes + TagSizeBytes + 1)
            {
                return string.Empty;
            }

            var nonce = packed.AsSpan(0, NonceSizeBytes);
            var tag = packed.AsSpan(NonceSizeBytes, TagSizeBytes);
            var cipher = packed.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
            return string.Empty;
        }
    }

    private static byte[]? GetKey()
    {
        lock (Gate)
        {
            if (_key is not null)
            {
                return _key;
            }

            try
            {
                var dataDirectory = ResolveDataDirectory();
                var folder = Path.Combine(dataDirectory, KeyFolderName);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, KeyFileName);

                if (!File.Exists(path))
                {
                    var created = RandomNumberGenerator.GetBytes(KeySizeBytes);
                    try
                    {
                        using var stream = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            KeySizeBytes,
                            FileOptions.WriteThrough);
                        stream.Write(created);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // Another PROTV instance/process created it first; read that one.
                    }

                    TryHide(path);
                }

                var key = File.ReadAllBytes(path);
                if (key.Length != KeySizeBytes)
                {
                    SetError($"The PROTV secret key file is {key.Length} bytes; expected {KeySizeBytes}.");
                    return null;
                }

                _lastError = null;
                _key = key;
                return _key;
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                return null;
            }
        }
    }

    // Folder for server-local PROTV secrets (<data>/protv). Used by the parental PIN store too.
    internal static string StorageFolder()
    {
        var folder = Path.Combine(ResolveDataDirectory(), KeyFolderName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_dataDirectory))
        {
            return _dataDirectory;
        }

        var configured = Plugin.Instance?.PluginPaths?.DataPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // <data>/plugins is the folder that contains PROTV.Plugin.dll; <data>/data is Jellyfin's
        // data path (where the VOD library also lives).
        var pluginsDirectory = ProTvRequirements.PluginsDirectory();
        if (!string.IsNullOrWhiteSpace(pluginsDirectory))
        {
            var root = Directory.GetParent(pluginsDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "data");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static void TryHide(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
        }
        catch
        {
            // Hiding is cosmetic; never fail because of it.
        }
    }

    private static void SetError(string message)
    {
        lock (Gate)
        {
            _lastError = message;
        }
    }
}

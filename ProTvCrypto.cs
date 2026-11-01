using System.Security.Cryptography;
using System.Text;

namespace PROTV.Plugin;

// PROTV returns the VOD stream links (MovieLinks[].Path / PathRTSP, EpisodeLinks[].Path) as
// AES-encrypted Base64; the desktop app decrypts them before playing. The key material is a fixed
// part of the PROTV protocol (it ships inside the desktop app), not a user secret, so the same
// values are used here.
//
// Recovered from the captured traffic (2.pcapng / 3.pcapng) together with the known plaintext of
// movie 10267 ("Animals"):
//   AES-128-CBC, PKCS#7
//   key = PBKDF2(password "MAKV2SPBNI99212", salt "MAKV2SPBNI99212_makv2spbni99212", 64 iters, SHA1), 16 bytes
//   IV  = ASCII "Ivan Medvedevdevdev", first 16 bytes
internal static class ProTvCrypto
{
    private const string Password = "MAKV2SPBNI99212";
    private const string Salt = "MAKV2SPBNI99212_makv2spbni99212";
    private const string IvSeed = "Ivan Medvedevdevdev";
    private const int Iterations = 64;
    private const int KeySizeBytes = 16;
    private const int BlockSizeBytes = 16;

    // The key never changes, so it is derived once instead of on every link.
    private static readonly Lazy<byte[]> DerivedKey = new(() => Rfc2898DeriveBytes.Pbkdf2(
        Password,
        Encoding.ASCII.GetBytes(Salt),
        Iterations,
        HashAlgorithmName.SHA1,
        KeySizeBytes));

    // Decrypts one link value. Returns null (never throws) when the value is missing or is not a
    // valid ciphertext, so one bad link can never break a sync.
    public static string? TryDecrypt(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(cipherText.Trim());
            if (cipher.Length == 0 || cipher.Length % BlockSizeBytes != 0)
            {
                return null;
            }

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DerivedKey.Value;
            aes.IV = Iv();

            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            var url = Encoding.UTF8.GetString(plain).Trim().Trim('\0');
            return url.Length == 0 ? null : url;
        }
        catch (Exception)
        {
            // Not a PROTV link (or the key changed): treat it as "no URL".
            return null;
        }
    }

    private static byte[] Iv()
    {
        var seed = Encoding.ASCII.GetBytes(IvSeed);
        var iv = new byte[BlockSizeBytes];
        Array.Copy(seed, iv, Math.Min(seed.Length, BlockSizeBytes));
        return iv;
    }
}

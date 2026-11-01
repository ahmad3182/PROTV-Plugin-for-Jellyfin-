using System.Text.Json;
using System.Xml.Serialization;

namespace PROTV.Plugin.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "protv-tests-" + Guid.NewGuid().ToString("N"));

    public SecretStoreTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        ProTvSecretStore.ConfigureForTests(_dataDirectory);
    }

    public void Dispose()
    {
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
    public void Protect_round_trips_the_password_and_hides_the_plain_text()
    {
        var protectedValue = ProTvSecretStore.Protect("correct horse battery staple");

        Assert.StartsWith("enc:v1:", protectedValue);
        Assert.DoesNotContain("correct horse battery staple", protectedValue);
        Assert.Equal("correct horse battery staple", ProTvSecretStore.Unprotect(protectedValue));
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "protv", "secret.key")));
    }

    [Fact]
    public void Protect_is_stable_for_an_already_protected_value()
    {
        var once = ProTvSecretStore.Protect("same-password");

        Assert.Equal(once, ProTvSecretStore.Protect(once));
        Assert.True(ProTvSecretStore.IsProtected(once));
        Assert.False(ProTvSecretStore.NeedsProtection(once));
    }

    [Fact]
    public void Empty_and_plain_text_values_keep_their_meaning()
    {
        Assert.Equal(string.Empty, ProTvSecretStore.Protect(string.Empty));
        Assert.Equal(string.Empty, ProTvSecretStore.Unprotect(string.Empty));
        Assert.Equal("legacy", ProTvSecretStore.Unprotect("legacy"));
        Assert.True(ProTvSecretStore.NeedsProtection("legacy"));
        Assert.False(ProTvSecretStore.NeedsProtection(string.Empty));
        Assert.False(ProTvSecretStore.IsProtected("legacy"));
    }

    [Fact]
    public void A_corrupt_or_foreign_envelope_decrypts_to_empty()
    {
        var protectedValue = ProTvSecretStore.Protect("do-not-send-ciphertext-as-a-password");

        File.Delete(Path.Combine(_dataDirectory, "protv", "secret.key"));
        ProTvSecretStore.ConfigureForTests(_dataDirectory);

        Assert.Equal(string.Empty, ProTvSecretStore.Unprotect(protectedValue));
        Assert.Equal(string.Empty, ProTvSecretStore.Unprotect("enc:v1:not-base64"));
    }

    [Fact]
    public void CanDecrypt_only_accepts_readable_encrypted_envelopes()
    {
        var protectedValue = ProTvSecretStore.Protect("readable");

        Assert.True(ProTvSecretStore.CanDecrypt(protectedValue));
        Assert.True(ProTvSecretStore.CanDecrypt(string.Empty));
        Assert.False(ProTvSecretStore.CanDecrypt("legacy-plain-text"));
        Assert.False(ProTvSecretStore.CanDecrypt("enc:v1:not-base64"));
    }

    [Fact]
    public void Protect_and_unprotect_are_safe_under_concurrency()
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        Parallel.For(0, 40, index =>
        {
            var value = "password-" + index;
            var roundTrip = ProTvSecretStore.Unprotect(ProTvSecretStore.Protect(value));
            if (!string.Equals(value, roundTrip, StringComparison.Ordinal))
            {
                failures.Enqueue(value);
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void Plugin_configuration_serializes_the_envelope_never_the_plain_text()
    {
        var configuration = new PluginConfiguration { Password = "a-real-password" };

        Assert.StartsWith("enc:v1:", configuration.Password);
        Assert.Equal("a-real-password", configuration.PasswordValue);

        var json = JsonSerializer.Serialize(configuration);
        Assert.DoesNotContain("a-real-password", json);
        Assert.Contains("enc:v1:", json);
        Assert.DoesNotContain("PasswordValue", json);

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);
        var xml = writer.ToString();
        Assert.DoesNotContain("a-real-password", xml);
        Assert.Contains("enc:v1:", xml);
    }
}

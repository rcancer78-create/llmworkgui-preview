using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiReadContractReviewTests
{
    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("empty")]
    [InlineData("embedded-null")]
    public async Task DecryptableCorruptLegacyBytesCannotBecomeUsableCredentials(string kind)
    {
        using var directory = new TestDirectory();
        var store = new DpapiSecretStore(directory.GetPath("secrets"));
        var reference = await store.SaveSecretAsync("synthetic-initial-credential");
        var plaintext = kind switch
        {
            "invalid-utf8" => new byte[] { 0xFF, 0xFF },
            "empty" => Array.Empty<byte>(),
            _ => Encoding.UTF8.GetBytes("synthetic\0credential")
        };
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Encoding.UTF8.GetBytes("LLMWorkGUI.SecretStore.v1"), DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(Assert.Single(Directory.GetFiles(store.SecretsDirectory)), "LLMWGUS1"u8.ToArray().Concat(encrypted).ToArray());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSecretAsync(reference));
            Assert.DoesNotContain("synthetic", error.Message, StringComparison.OrdinalIgnoreCase);
            var probe = await store.ProbePayloadAsync(reference);
            Assert.False(probe.IsPresent);
            Assert.Equal(SecretStorageFailureReason.LegacyPayloadCorrupt, probe.Reason);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

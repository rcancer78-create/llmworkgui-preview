using System.Runtime.Versioning;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

[SupportedOSPlatform("windows")]
public sealed class LegacyPayloadProbeReviewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CorruptLegacyPayloadCannotBecomeAUsableCredential(int corruption)
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("secrets");
        var legacy = new DpapiSecretStore(path);
        var reference = await legacy.SaveSecretAsync("synthetic-owned-legacy-probe");
        var file = Path.Combine(path, SecretReference.GetIdentifier(reference) + ".secret");
        byte[] bytes = corruption switch { 0 => [], 1 => "invalid-header"u8.ToArray(), _ => "LLMWGUS1invalid-dpapi"u8.ToArray() };
        await File.WriteAllBytesAsync(file, bytes);
        var store = new CredentialManagerSecretStore(new FakeCredentialManagerApi(), path);
        var probe = await store.ProbePayloadAsync(reference);
        Assert.Equal(SecretPayloadPresence.Unavailable, probe.Presence);
        Assert.NotEqual(SecretStorageFailureReason.None, probe.Reason);
        Assert.False(await store.PayloadExistsAsync(reference));
        Assert.False(await legacy.PayloadExistsAsync(reference));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.False(store.HasCredentialAuthorityMarker(reference));
    }

    [Fact]
    public async Task ValidLegacyProbeIsReadOnlyAndMissingPayloadIsAbsent()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("secrets");
        var legacy = new DpapiSecretStore(path);
        var reference = await legacy.SaveSecretAsync("synthetic-owned-legacy-probe");
        var file = Path.Combine(path, SecretReference.GetIdentifier(reference) + ".secret");
        var bytes = await File.ReadAllBytesAsync(file);
        var store = new CredentialManagerSecretStore(new FakeCredentialManagerApi(), path);
        Assert.True((await store.ProbePayloadAsync(reference)).IsPresent);
        Assert.True(await legacy.PayloadExistsAsync(reference));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.False(store.HasCredentialAuthorityMarker(reference));
        await legacy.DeleteSecretAsync(reference);
        Assert.Equal(SecretPayloadPresence.Absent, (await store.ProbePayloadAsync(reference)).Presence);
    }
}

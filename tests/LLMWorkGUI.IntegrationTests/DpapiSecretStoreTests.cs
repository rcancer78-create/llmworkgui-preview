using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests : IDisposable
{
    private static readonly Regex SecretUrnPattern = new(
        @"^urn:llmworkgui:secret:[a-z0-9_-]+$",
        RegexOptions.CultureInvariant);

    private readonly TestDirectory _directory = new();
    private readonly DpapiSecretStore _store;

    public DpapiSecretStoreTests()
    {
        _store = new DpapiSecretStore(_directory.GetPath("secrets"));
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task SaveSecretAsync_ReturnsOpaqueUrnReference()
    {
        const string secret = "sk-live-abcdefghijklmnopqrstuvwxyz";

        var reference = await _store.SaveSecretAsync(secret);

        Assert.Matches(SecretUrnPattern, reference);
        Assert.DoesNotContain(secret, reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsSecretValue()
    {
        const string secret = "sk-live-abcdefghijklmnopqrstuvwxyz";

        var reference = await _store.SaveSecretAsync(secret);
        var loaded = await _store.GetSecretAsync(reference);

        Assert.Equal(secret, loaded);
    }

    [Fact]
    public async Task GetSecretAsync_DecryptsWithNewStoreInstance()
    {
        const string secret = "persisted-secret-value";

        var reference = await _store.SaveSecretAsync(secret);
        var reopened = new DpapiSecretStore(_directory.GetPath("secrets"));

        Assert.Equal(secret, await reopened.GetSecretAsync(reference));
    }

    [Fact]
    public async Task SaveSecretAsync_DoesNotPersistPlaintextOnDisk()
    {
        const string secret = "super-secret-api-key-9f7c1b2a";

        await _store.SaveSecretAsync(secret);

        var files = Directory.GetFiles(_store.SecretsDirectory);
        var fileBytes = await File.ReadAllBytesAsync(Assert.Single(files));

        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.UTF32, Encoding.ASCII })
        {
            Assert.Equal(-1, fileBytes.AsSpan().IndexOf(encoding.GetBytes(secret)));
        }
    }

    [Fact]
    public async Task SaveSecretAsync_GeneratesUniqueReferences()
    {
        var first = await _store.SaveSecretAsync("first-secret-value");
        var second = await _store.SaveSecretAsync("second-secret-value");

        Assert.NotEqual(first, second);
        Assert.Equal("first-secret-value", await _store.GetSecretAsync(first));
        Assert.Equal("second-secret-value", await _store.GetSecretAsync(second));
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNullForMissingSecret()
    {
        var reference = SecretReference.Create("missing123");

        Assert.Null(await _store.GetSecretAsync(reference));
    }

    [Fact]
    public async Task DeleteSecretAsync_RemovesStoredSecret()
    {
        var reference = await _store.SaveSecretAsync("to-be-deleted-secret");

        Assert.True(await _store.DeleteSecretAsync(reference));
        Assert.Null(await _store.GetSecretAsync(reference));
        Assert.False(await _store.DeleteSecretAsync(reference));
        Assert.Empty(Directory.GetFiles(_store.SecretsDirectory));
    }

    [Fact]
    public async Task SaveSecretAsync_RejectsEmptySecret()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveSecretAsync(string.Empty));
    }

    [Fact]
    public async Task OverwriteSecretAsync_ReplacesTheValueBehindTheSameReference()
    {
        const string original = "sk-original-value-111";
        const string rotated = "sk-rotated-value-222";

        var reference = await _store.SaveSecretAsync(original);
        await _store.OverwriteSecretAsync(reference, rotated);

        // ADR-0005 §5.1: the rotation overwrites the value at the same target, so the reference that
        // profiles and accounts already store keeps resolving.
        Assert.Equal(rotated, await _store.GetSecretAsync(reference));
        Assert.Single(Directory.GetFiles(_store.SecretsDirectory));
    }

    [Fact]
    public async Task OverwriteSecretAsync_DoesNotLeaveThePreviousValueOnDisk()
    {
        const string original = "sk-original-value-333";
        const string rotated = "sk-rotated-value-444";

        var reference = await _store.SaveSecretAsync(original);
        await _store.OverwriteSecretAsync(reference, rotated);

        var fileBytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_store.SecretsDirectory)));

        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.UTF32, Encoding.ASCII })
        {
            Assert.Equal(-1, fileBytes.AsSpan().IndexOf(encoding.GetBytes(original)));
        }
    }

    [Fact]
    public async Task OverwriteSecretAsync_RejectsEmptySecretAndMalformedReference()
    {
        var reference = await _store.SaveSecretAsync("sk-present-value");

        await Assert.ThrowsAsync<ArgumentException>(() => _store.OverwriteSecretAsync(reference, string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.OverwriteSecretAsync("urn:llmworkgui:secret:OpenAI", "sk-value"));
    }

    [Fact]
    public async Task PayloadExistsAsync_ReportsWhetherTheValueCanStillBeRead()
    {
        var reference = await _store.SaveSecretAsync("sk-present-value");

        Assert.True(await _store.PayloadExistsAsync(reference));

        // A payload that is gone is what makes a reference Missing, for example after a database
        // restore that does not restore secrets (ADR-0005 §5.3).
        Assert.True(await _store.DeleteSecretAsync(reference));
        Assert.False(await _store.PayloadExistsAsync(reference));
    }

    [Fact]
    public async Task PayloadExistsAsync_RejectsMalformedReference()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.PayloadExistsAsync("urn:llmworkgui:secret:openai/api"));
    }

    [Fact]
    public void Store_ImplementsThePayloadManagerCapability()
    {
        // The lifecycle can only rotate in place and report Missing when the store exposes this
        // capability, so the DPAPI store must keep implementing it.
        Assert.IsAssignableFrom<ISecretPayloadManager>(_store);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-urn")]
    [InlineData("urn:llmworkgui:secret:")]
    [InlineData("urn:llmworkgui:secret:OpenAI")]
    [InlineData("urn:llmworkgui:secret:openai.api")]
    [InlineData("urn:llmworkgui:secret:..%2Fescape")]
    [InlineData("urn:llmworkgui:secret:openai/api")]
    [InlineData(" urn:llmworkgui:secret:openai")]
    public async Task GetSecretAsync_RejectsMalformedReferences(string reference)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetSecretAsync(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteSecretAsync(reference));
    }

    [Fact]
    public void SecretReference_CreateAndGetIdentifier_RoundTrips()
    {
        var reference = SecretReference.Create("openai-api-key");

        Assert.Equal("urn:llmworkgui:secret:openai-api-key", reference);
        Assert.Equal("openai-api-key", SecretReference.GetIdentifier(reference));
        Assert.True(SecretReference.IsValid(reference));
        Assert.False(SecretReference.IsValid("urn:llmworkgui:secret:OpenAI"));
    }
}

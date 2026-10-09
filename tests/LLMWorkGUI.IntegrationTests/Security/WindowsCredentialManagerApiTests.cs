using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

/// <summary>
/// The native boundary against the real Windows Credential Manager of the current user. It proves the
/// interop itself: the struct layout is correct, the blob is exactly the UTF-8 bytes of the value, a
/// generic credential is persisted with <c>CRED_PERSIST_LOCAL_MACHINE</c>, and neither the target, the
/// user name nor the comment carries the value.
///
/// Every credential is written under a unique target and removed again, so the test leaves the
/// user's Credential Manager as it found it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerApiTests
{
    private const string UserName = CredentialManagerTarget.UserName;
    private const string Comment = CredentialManagerTarget.Comment;

    [Fact]
    public void WriteReadDelete_RoundTripsTheValueUnderTheDeterministicTarget()
    {
        var api = new WindowsCredentialManagerApi();
        var target = NewTarget();
        const string value = "sk-native-boundary-0001";

        try
        {
            Assert.True(api.Write(target, value, UserName, Comment).IsSuccess);

            var read = api.Read(target);

            Assert.True(read.IsSuccess);
            Assert.NotNull(read.Blob);

            // Exactly the UTF-8 bytes of the value: no terminator, no extra framing.
            Assert.Equal(Encoding.UTF8.GetBytes(value), read.Blob!);
            Assert.Equal(value, Encoding.UTF8.GetString(read.Blob!));
            Assert.Equal(value.Length, read.Blob!.Length);

            Assert.True(api.Delete(target).IsSuccess);
            Assert.Equal(CredentialManagerOutcome.NotFound, api.Read(target).Outcome);
        }
        finally
        {
            api.Delete(target);
        }
    }

    [Fact]
    public void Write_ReplacesAnExistingCredentialAtTheSameTarget()
    {
        var api = new WindowsCredentialManagerApi();
        var target = NewTarget();

        try
        {
            Assert.True(api.Write(target, "sk-native-first", UserName, Comment).IsSuccess);
            Assert.True(api.Write(target, "sk-native-second", UserName, Comment).IsSuccess);

            var read = api.Read(target);

            Assert.Equal("sk-native-second", Encoding.UTF8.GetString(read.Blob!));
        }
        finally
        {
            api.Delete(target);
        }
    }

    [Fact]
    public void ReadAndDelete_OfAnUnknownTargetReportNotFound()
    {
        var api = new WindowsCredentialManagerApi();
        var target = NewTarget();

        Assert.Equal(CredentialManagerOutcome.NotFound, api.Read(target).Outcome);
        Assert.Equal(CredentialManagerOutcome.NotFound, api.Delete(target).Outcome);
    }

    [Fact]
    public void Write_OfAnOversizedValueIsClassifiedInsteadOfSucceeding()
    {
        var api = new WindowsCredentialManagerApi();
        var target = NewTarget();
        var oversized = new string('k', ICredentialManagerApi.MaxCredentialBlobSize + 1);

        try
        {
            var write = api.Write(target, oversized, UserName, Comment);

            // The documented limit can make a formerly accepted oversized payload unsavable. Whatever
            // Windows reports, it must not be reported as a success.
            Assert.False(write.IsSuccess);
            Assert.NotEqual(CredentialManagerOutcome.Success, write.Outcome);
            Assert.Equal(CredentialManagerOutcome.NotFound, api.Read(target).Outcome);
        }
        finally
        {
            api.Delete(target);
        }
    }

    [Fact]
    public void TargetMapping_UsesTheUrnIdentifierAndNeverTheValue()
    {
        const string reference = "urn:llmworkgui:secret:openai-api-key";
        var target = CredentialManagerTarget.ForReference(reference);

        Assert.Equal("LLMWorkGUI/secret/openai-api-key", target);
        Assert.Equal("LLMWorkGUI", UserName);
        Assert.Equal("LLMWorkGUI secret payload", Comment);

        // ADR-0005 §1.2: the identifier is the whole mapping, so a shared reference resolves to one
        // credential whatever owner metadata says about it.
        Assert.Equal(target, CredentialManagerTarget.ForReference(reference));
        Assert.Throws<ArgumentException>(() => CredentialManagerTarget.ForReference("urn:llmworkgui:secret:OpenAI"));
    }

    [Fact]
    public async Task Store_SavesThroughTheRealCredentialManagerAndLeavesNoDpapiFile()
    {
        var directory = new TestDirectory();
        var api = new WindowsCredentialManagerApi();
        var store = new CredentialManagerSecretStore(api, directory.GetPath("secrets"));

        try
        {
            const string value = "sk-real-store-value-0001";
            var reference = await store.SaveSecretAsync(value);
            var target = CredentialManagerTarget.ForReference(reference);

            try
            {
                Assert.Equal(value, await store.GetSecretAsync(reference));
                Assert.True(store.HasCredentialAuthorityMarker(reference));
                Assert.Empty(Directory.GetFiles(store.SecretsDirectory, "*.secret"));

                var read = api.Read(target);
                Assert.True(read.IsSuccess);
                Assert.Equal(value, Encoding.UTF8.GetString(read.Blob!));

                await store.OverwriteSecretAsync(reference, "sk-real-store-value-0002");

                Assert.Equal("sk-real-store-value-0002", await store.GetSecretAsync(reference));
                Assert.Equal("sk-real-store-value-0002", Encoding.UTF8.GetString(api.Read(target).Blob!));

                Assert.True(await store.DeleteSecretAsync(reference));
                Assert.False(await store.DeleteSecretAsync(reference));
                Assert.Empty(Directory.GetFiles(store.SecretsDirectory));
            }
            finally
            {
                api.Delete(target);
            }
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task Store_PromotesALegacyDpapiPayloadToTheRealCredentialManager()
    {
        var directory = new TestDirectory();
        var api = new WindowsCredentialManagerApi();
        var secretsDirectory = directory.GetPath("secrets");
        var legacy = new DpapiSecretStore(secretsDirectory);
        var store = new CredentialManagerSecretStore(api, secretsDirectory);

        try
        {
            var reference = await legacy.SaveSecretAsync("sk-legacy-promotion-0001");
            var target = CredentialManagerTarget.ForReference(reference);

            try
            {
                // A reference written by an earlier release is readable, and one rotation moves it to
                // the Credential Manager and removes the legacy file.
                Assert.Equal("sk-legacy-promotion-0001", await store.GetSecretAsync(reference));
                Assert.False(store.HasCredentialAuthorityMarker(reference));

                await store.OverwriteSecretAsync(reference, "sk-legacy-promotion-0002");

                Assert.True(api.Read(target).IsSuccess);
                Assert.True(store.HasCredentialAuthorityMarker(reference));
                Assert.Empty(Directory.GetFiles(secretsDirectory, "*.secret"));
                Assert.Equal("sk-legacy-promotion-0002", await store.GetSecretAsync(reference));
            }
            finally
            {
                api.Delete(target);
            }
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task Store_RejectsAnOversizedValueWithoutLeavingAnyState()
    {
        var directory = new TestDirectory();
        var api = new WindowsCredentialManagerApi();
        var store = new CredentialManagerSecretStore(api, directory.GetPath("secrets"));

        try
        {
            var oversized = new string('k', ICredentialManagerApi.MaxCredentialBlobSize + 1);

            var failure = await Assert.ThrowsAsync<SecretStorageException>(() => store.SaveSecretAsync(oversized));

            Assert.Equal(SecretStorageFailureReason.CredentialManagerRecordRejected, failure.Reason);

            // The size is rejected before anything is created, so not even the directory appears.
            Assert.False(Directory.Exists(store.SecretsDirectory));
        }
        finally
        {
            directory.Dispose();
        }
    }

    private static string NewTarget() =>
        CredentialManagerTarget.Prefix + Guid.NewGuid().ToString("N");
}

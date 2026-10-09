using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

/// <summary>
/// The storage procedure of the primary store, exercised against a scriptable Credential Manager
/// boundary so the failure windows Windows does not reproduce on demand are still covered: a refused
/// write, a refused read, an unreadable record and a classified outage (ADR-0005 §1.1-§1.5).
///
/// Every assertion is about which representation is authoritative, never about a value being
/// inferred from metadata.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerSecretStoreTests : IDisposable
{
    private const string Secret = "sk-procedure-value-0001";
    private const string Rotated = "sk-procedure-value-0002";

    private readonly TestDirectory _directory = new();
    private readonly FakeCredentialManagerApi _credentials = new();
    private readonly DpapiSecretStore _legacy;
    private readonly CredentialManagerSecretStore _store;

    public CredentialManagerSecretStoreTests()
    {
        _legacy = new DpapiSecretStore(SecretsDirectory);
        _store = new CredentialManagerSecretStore(_credentials, SecretsDirectory);
    }

    public void Dispose() => _directory.Dispose();

    private string SecretsDirectory => _directory.GetPath("secrets");

    [Fact]
    public async Task SaveSecretAsync_WritesExactlyOneBackend()
    {
        var reference = await _store.SaveSecretAsync(Secret);

        // ADR-0005 §1.1: the Credential Manager is the primary storage, so a new payload never
        // produces a DPAPI file.
        Assert.Equal(Secret, await _store.GetSecretAsync(reference));
        Assert.True(_store.HasCredentialAuthorityMarker(reference));
        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.secret"));
        Assert.Single(Directory.GetFiles(SecretsDirectory, "*.cmref"));
        Assert.Equal(Secret, _credentials.StoredValue(CredentialManagerTarget.ForReference(reference)));
    }

    [Fact]
    public async Task SaveSecretAsync_StaysReadableThroughASecondStoreInstance()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var reopened = new CredentialManagerSecretStore(_credentials, SecretsDirectory);

        Assert.Equal(Secret, await reopened.GetSecretAsync(reference));
        Assert.True(reopened.HasCredentialAuthorityMarker(reference));
    }

    [Fact]
    public async Task EveryCallerForAReferenceAddressesOneSingleTarget()
    {
        // A provider profile, an account and an unbound SaveSecretAsync call all resolve the same
        // URN, so the deterministic target leaves exactly one credential behind (ADR-0005 §1.2).
        var reference = await _store.SaveSecretAsync(Secret);
        var target = CredentialManagerTarget.ForReference(reference);

        Assert.Equal("LLMWorkGUI/secret/" + SecretReference.GetIdentifier(reference), target);
        Assert.Equal(target, CredentialManagerTarget.ForReference(reference));

        await _store.OverwriteSecretAsync(reference, Rotated);
        await _store.OverwriteSecretAsync(reference, Secret);

        Assert.Equal(1, _credentials.StoredCredentialCount);
        Assert.All(_credentials.Calls, call => Assert.Equal(target, call.Target));
        Assert.Equal(Secret, await _store.GetSecretAsync(reference));
    }

    [Fact]
    public async Task WrittenCredentialMetadataNeverCarriesTheSecretValue()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var write = Assert.Single(_credentials.CallsFor("Write"));

        Assert.DoesNotContain(Secret, write.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, write.UserName!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, write.Comment!, StringComparison.Ordinal);
        Assert.Equal(CredentialManagerTarget.UserName, write.UserName);
        Assert.Equal(CredentialManagerTarget.Comment, write.Comment);

        var marker = await File.ReadAllTextAsync(MarkerPath(reference));
        Assert.DoesNotContain(Secret, marker, StringComparison.Ordinal);
        Assert.Equal(reference, marker.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
    }

    [Fact]
    public async Task CredWriteFailure_PreservesThePreviousValueAndWritesNoDpapiFile()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        _credentials.WriteOutcomeOverride = CredentialManagerOutcome.Failed;

        var failure = await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.OverwriteSecretAsync(reference, Rotated));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerOperationFailed, failure.Reason);
        Assert.Equal(Secret, _credentials.StoredValue(CredentialManagerTarget.ForReference(reference)));
        Assert.Equal(Secret, await _store.GetSecretAsync(reference));
        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.secret"));
    }

    [Fact]
    public async Task CredWriteQuotaRejection_FailsClosedWithoutMarkerOrDpapiFile()
    {
        _credentials.WriteOutcomeOverride = CredentialManagerOutcome.InvalidParameter;

        var failure = await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.SaveSecretAsync(Secret));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerRecordRejected, failure.Reason);
        Assert.Empty(Directory.GetFiles(SecretsDirectory));
        Assert.Equal(0, _credentials.StoredCredentialCount);
    }

    [Fact]
    public async Task AccessDeniedOnRead_FailsClosedAndNeverOpensTheLegacyFile()
    {
        // A surviving legacy payload must not become readable just because the Credential Manager
        // refused to answer.
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-9999");
        _credentials.ReadOutcomeOverride = CredentialManagerOutcome.AccessDenied;

        var failure = await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.GetSecretAsync(legacyReference));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerAccessDenied, failure.Reason);
        Assert.DoesNotContain("sk-legacy-value-9999", failure.Message, StringComparison.Ordinal);

        var probe = await _store.ProbePayloadAsync(legacyReference);

        Assert.False(probe.IsPresent);
        Assert.Equal(SecretStorageFailureReason.CredentialManagerAccessDenied, probe.Reason);
    }

    [Fact]
    public async Task NoLogonSessionAndUnclassifiedFailuresAlsoFailClosed()
    {
        var reference = await _store.SaveSecretAsync(Secret);

        foreach (var (outcome, reason) in new[]
                 {
                     (CredentialManagerOutcome.NoLogonSession, SecretStorageFailureReason.CredentialManagerNoLogonSession),
                     (CredentialManagerOutcome.NotSupported, SecretStorageFailureReason.CredentialManagerNotSupported),
                     (CredentialManagerOutcome.Failed, SecretStorageFailureReason.CredentialManagerOperationFailed)
                 })
        {
            _credentials.ReadOutcomeOverride = outcome;

            var failure = await Assert.ThrowsAsync<SecretStorageException>(() => _store.GetSecretAsync(reference));

            Assert.Equal(reason, failure.Reason);

            var probe = await _store.ProbePayloadAsync(reference);

            Assert.False(probe.IsPresent);
            Assert.Equal(SecretPayloadPresence.Unavailable, probe.Presence);
            Assert.Equal(reason, probe.Reason);
        }

        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.secret"));
    }

    [Fact]
    public async Task NoLogonSessionOnRead_FailsAnOverwriteWithoutAnyDpapiWrite()
    {
        // Absence is unproved, so a rotation must not write a legacy payload that a later successful
        // read could hide behind an older credential.
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-8888");
        _credentials.ReadOutcomeOverride = CredentialManagerOutcome.NoLogonSession;

        await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.OverwriteSecretAsync(legacyReference, Rotated));

        Assert.Equal("sk-legacy-value-8888", await _legacy.GetSecretAsync(legacyReference));
    }

    [Fact]
    public async Task NotSupportedOnOverwriteProbe_FailsWithoutAnyDpapiWrite()
    {
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-7777");
        _credentials.ReadOutcomeOverride = CredentialManagerOutcome.NotSupported;

        await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.OverwriteSecretAsync(legacyReference, Rotated));

        Assert.Equal("sk-legacy-value-7777", await _legacy.GetSecretAsync(legacyReference));
        Assert.False(_store.HasCredentialAuthorityMarker(legacyReference));
    }

    [Theory]
    [InlineData(CredentialManagerOutcome.NotSupported)]
    [InlineData(CredentialManagerOutcome.ApiUnavailable)]
    public async Task ClassifiedUnavailability_OnCreateWritesOnlyTheLegacyLayout(
        CredentialManagerOutcome outcome)
    {
        _credentials.WriteOutcomeOverride = outcome;

        var reference = await _store.SaveSecretAsync(Secret);

        // A DPAPI fallback write must leave no authority marker, otherwise the legacy value would be
        // suppressed on the way back.
        Assert.False(_store.HasCredentialAuthorityMarker(reference));
        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.cmref"));
        Assert.Single(Directory.GetFiles(SecretsDirectory, "*.secret"));
        Assert.Equal(Secret, await _store.GetSecretAsync(reference));
        Assert.Equal(0, _credentials.StoredCredentialCount);
    }

    [Theory]
    [InlineData(CredentialManagerOutcome.NotSupported)]
    [InlineData(CredentialManagerOutcome.ApiUnavailable)]
    public async Task ClassifiedUnavailability_OnOverwriteRestoresTheLegacyReadPath(
        CredentialManagerOutcome outcome)
    {
        // A reference that predates this release: a legacy payload and no credential.
        var legacyReference = await _legacy.SaveSecretAsync(Secret);
        _credentials.WriteOutcomeOverride = outcome;

        await _store.OverwriteSecretAsync(legacyReference, Rotated);

        Assert.False(_store.HasCredentialAuthorityMarker(legacyReference));
        Assert.Equal(Rotated, await _store.GetSecretAsync(legacyReference));
        Assert.Equal(Rotated, await _legacy.GetSecretAsync(legacyReference));
    }

    [Fact]
    public async Task ClassifiedUnavailability_DoesNotFallBackWhenAMarkerAlreadyExisted()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        _credentials.ReadOutcomeOverride = CredentialManagerOutcome.NotFound;
        _credentials.WriteOutcomeOverride = CredentialManagerOutcome.NotSupported;

        // A marker that already exists means a Credential Manager value may have been stored, so a
        // legacy write could resurrect a value the reference no longer means.
        var failure = await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.OverwriteSecretAsync(reference, Rotated));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerNotSupported, failure.Reason);
        Assert.True(_store.HasCredentialAuthorityMarker(reference));
        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.secret"));
    }

    [Fact]
    public async Task AuthorityMarker_BlocksTheLegacyFallbackAfterAnOutOfBandCredentialDeletion()
    {
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-6666");
        _credentials.WriteOutcomeOverride = CredentialManagerOutcome.NotSupported;
        await _store.OverwriteSecretAsync(legacyReference, Rotated);
        _credentials.WriteOutcomeOverride = null;

        // A first rotation promotes the reference to the Credential Manager, and a stale legacy file
        // is left behind on purpose.
        await _store.OverwriteSecretAsync(legacyReference, Rotated);
        await File.WriteAllBytesAsync(LegacyPath(legacyReference), Encoding.UTF8.GetBytes("stale"));

        _credentials.Delete(CredentialManagerTarget.ForReference(legacyReference));

        Assert.Null(await _store.GetSecretAsync(legacyReference));
        Assert.False(await _store.PayloadExistsAsync(legacyReference));
    }

    [Fact]
    public async Task LegacyPayloadWithoutAMarkerIsStillReadable()
    {
        // The compatibility path a reference written by an earlier release depends on.
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-5555");

        Assert.False(_store.HasCredentialAuthorityMarker(legacyReference));
        Assert.Equal("sk-legacy-value-5555", await _store.GetSecretAsync(legacyReference));
        Assert.True(await _store.PayloadExistsAsync(legacyReference));
    }

    [Fact]
    public async Task CredWriteSuccessAndFailedLegacyDeleteStillReturnsTheCredentialManagerValue()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var legacyPath = LegacyPath(reference);
        await File.WriteAllBytesAsync(legacyPath, Encoding.UTF8.GetBytes("stale"));
        File.SetAttributes(legacyPath, FileAttributes.ReadOnly);

        try
        {
            // The credential is authoritative the moment CredWrite succeeded, so a legacy file that
            // cannot be removed is not a failure and is retried later.
            await _store.OverwriteSecretAsync(reference, Rotated);

            Assert.Equal(Rotated, await _store.GetSecretAsync(reference));
            Assert.True(File.Exists(legacyPath));

            File.SetAttributes(legacyPath, FileAttributes.Normal);
            await _store.OverwriteSecretAsync(reference, Secret);

            Assert.False(File.Exists(legacyPath));
            Assert.Equal(Secret, await _store.GetSecretAsync(reference));
        }
        finally
        {
            if (File.Exists(legacyPath))
            {
                File.SetAttributes(legacyPath, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task PayloadExists_StaysTrueForACredentialWhoseLegacyFileIsGone()
    {
        var reference = await _store.SaveSecretAsync(Secret);

        Assert.Empty(Directory.GetFiles(SecretsDirectory, "*.secret"));
        Assert.True(await _store.PayloadExistsAsync(reference));
    }

    [Fact]
    public async Task CorruptCredential_FailsClosedWithoutReadingTheLegacyFile()
    {
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-4444");
        var target = CredentialManagerTarget.ForReference(legacyReference);

        foreach (var blob in new[]
                 {
                     Array.Empty<byte>(),
                     new byte[] { 0xFF, 0xFE, 0xFD },
                     Encoding.UTF8.GetBytes(Secret + "\0")
                 })
        {
            _credentials.SeedRaw(target, blob);

            var failure = await Assert.ThrowsAsync<SecretStorageException>(
                () => _store.GetSecretAsync(legacyReference));

            Assert.Equal(SecretStorageFailureReason.CredentialManagerRecordCorrupt, failure.Reason);
            Assert.Equal(SecretPayloadPresence.Unavailable, (await _store.ProbePayloadAsync(legacyReference)).Presence);

            await Assert.ThrowsAsync<SecretStorageException>(
                () => _store.OverwriteSecretAsync(legacyReference, Rotated));
        }

        // A corrupt record is a failure, never a hint that the legacy payload is still usable.
        Assert.Equal("sk-legacy-value-4444", await _legacy.GetSecretAsync(legacyReference));
    }

    [Fact]
    public async Task CorruptCredential_IsStillDeletable()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        _credentials.SeedRaw(CredentialManagerTarget.ForReference(reference), Array.Empty<byte>());

        Assert.True(await _store.DeleteSecretAsync(reference));
        Assert.False(await _store.DeleteSecretAsync(reference));
        Assert.False(_store.HasCredentialAuthorityMarker(reference));
    }

    [Fact]
    public async Task DeleteSecretAsync_RemovesBothRepresentationsAndThenReportsAbsent()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var legacyPath = LegacyPath(reference);
        await File.WriteAllBytesAsync(legacyPath, Encoding.UTF8.GetBytes("stale"));

        Assert.True(await _store.DeleteSecretAsync(reference));
        Assert.False(File.Exists(legacyPath));
        Assert.Equal(0, _credentials.StoredCredentialCount);
        Assert.False(_store.HasCredentialAuthorityMarker(reference));
        Assert.Empty(Directory.GetFiles(SecretsDirectory));

        Assert.False(await _store.DeleteSecretAsync(reference));
    }

    [Fact]
    public async Task DeleteSecretAsync_TreatsAMissingCredentialAsAlreadyAbsent()
    {
        var legacyReference = await _legacy.SaveSecretAsync("sk-legacy-value-3333");

        // Only the legacy payload exists: CredDelete answers 1168, which is a state and not an error.
        Assert.True(await _store.DeleteSecretAsync(legacyReference));
        Assert.Null(await _legacy.GetSecretAsync(legacyReference));
        Assert.False(await _store.DeleteSecretAsync(legacyReference));
    }

    [Fact]
    public async Task DeleteSecretAsync_FailureDoesNotCallCredDeleteOrRecreateTheFile()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var legacyPath = LegacyPath(reference);
        await File.WriteAllBytesAsync(legacyPath, Encoding.UTF8.GetBytes("stale"));
        File.SetAttributes(legacyPath, FileAttributes.ReadOnly);

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => _store.DeleteSecretAsync(reference));

            // A file that cannot be removed must not be followed by a credential deletion.
            Assert.Empty(_credentials.CallsFor("Delete"));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(Secret, await _store.GetSecretAsync(reference));
        }
        finally
        {
            if (File.Exists(legacyPath))
            {
                File.SetAttributes(legacyPath, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task DeleteSecretAsync_ClassifiedCredDeleteFailureFailsAndKeepsTheCredential()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        var legacyPath = LegacyPath(reference);
        await File.WriteAllBytesAsync(legacyPath, Encoding.UTF8.GetBytes("stale"));
        _credentials.DeleteOutcomeOverride = CredentialManagerOutcome.AccessDenied;

        var failure = await Assert.ThrowsAsync<SecretStorageException>(
            () => _store.DeleteSecretAsync(reference));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerAccessDenied, failure.Reason);
        Assert.True(_store.HasCredentialAuthorityMarker(reference));
        Assert.Equal(Secret, await _store.GetSecretAsync(reference));

        // The file was already removed and is never recreated.
        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public async Task OversizedSecret_IsRejectedWithATypedErrorAndLeavesNoState()
    {
        var oversized = new string('k', ICredentialManagerApi.MaxCredentialBlobSize + 1);

        var failure = await Assert.ThrowsAsync<SecretStorageException>(() => _store.SaveSecretAsync(oversized));

        Assert.Equal(SecretStorageFailureReason.CredentialManagerRecordRejected, failure.Reason);

        // The size is rejected before anything is created, so not even the directory appears.
        Assert.False(Directory.Exists(SecretsDirectory));
        Assert.Equal(0, _credentials.StoredCredentialCount);
        Assert.Empty(_credentials.Calls);
    }

    [Fact]
    public async Task Cancellation_PropagatesOutOfThePayloadProbe()
    {
        var reference = await _store.SaveSecretAsync(Secret);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.ProbePayloadAsync(reference, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.PayloadExistsAsync(reference, cancellation.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-urn")]
    [InlineData("urn:llmworkgui:secret:")]
    [InlineData("urn:llmworkgui:secret:OpenAI")]
    [InlineData("urn:llmworkgui:secret:openai/api")]
    public async Task MalformedReferences_KeepFailingWithArgumentException(string reference)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetSecretAsync(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteSecretAsync(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PayloadExistsAsync(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OverwriteSecretAsync(reference, Secret));
    }

    [Fact]
    public void Store_ImplementsThePayloadManagerCapability()
    {
        // The lifecycle can only rotate in place and report Missing when the store exposes this.
        Assert.IsAssignableFrom<ISecretPayloadManager>(_store);
        Assert.IsAssignableFrom<ISecretStore>(_store);
    }

    [Fact]
    public async Task SaveSecretAsync_RejectsEmptySecret()
    {
        var reference = await _store.SaveSecretAsync(Secret);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveSecretAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OverwriteSecretAsync(reference, string.Empty));
    }

    private string LegacyPath(string reference) =>
        Path.Combine(SecretsDirectory, SecretReference.GetIdentifier(reference) + ".secret");

    private string MarkerPath(string reference) =>
        Path.Combine(SecretsDirectory, SecretReference.GetIdentifier(reference) + ".cmref");
}

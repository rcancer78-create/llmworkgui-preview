using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Application.Security;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// The production <see cref="ISecretStore"/>: per-user generic credentials in the Windows Credential
/// Manager, with the DPAPI payload of an earlier release kept readable as a legacy representation
/// (ADR-0005 §1.1-§1.5).
///
/// Three rules make the two representations safe to coexist:
///
/// <list type="number">
/// <item>A credential that exists is authoritative. The legacy file is opened only when
/// <c>CredRead</c> answers <c>1168</c> <em>and</em> no authority marker exists for the reference.</item>
/// <item>A save writes exactly one backend. The authority marker is created before <c>CredWrite</c>
/// and removed again if that write fails, so the legacy file can never become a silent fallback for
/// a value the Credential Manager was supposed to own.</item>
/// <item>Nothing but the Credential Manager is authoritative after the marker exists, so an
/// out-of-band credential deletion produces <c>Missing</c> instead of resurrecting an older value.</item>
/// </list>
///
/// Crash window: a process that dies between marker creation and <c>CredWrite</c> leaves a legacy
/// secret temporarily unreadable. It can never resurrect a stale value, which is the property that
/// matters; the user re-enters the value and the reference resolves again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerSecretStore : ISecretStore, ISecretPayloadManager
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ICredentialManagerApi _credentials;
    private readonly DpapiSecretStore _legacy;
    private readonly CredentialAuthorityMarkerStore _markers;
    private readonly ILogger<CredentialManagerSecretStore>? _logger;

    public CredentialManagerSecretStore(
        ICredentialManagerApi credentials,
        string? secretsDirectory = null,
        ILogger<CredentialManagerSecretStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        _credentials = credentials;
        _legacy = new DpapiSecretStore(secretsDirectory);
        _markers = new CredentialAuthorityMarkerStore(_legacy.SecretsDirectory);
        _logger = logger;
    }

    public string SecretsDirectory => _legacy.SecretsDirectory;

    /// <summary>
    /// True when the Credential Manager owns the reference, which is what makes the legacy file a
    /// non-fallback. Exposed for diagnostics and tests; it reveals nothing but the storage decision.
    /// </summary>
    public bool HasCredentialAuthorityMarker(string secretReference) =>
        _markers.Exists(SecretReference.GetIdentifier(secretReference));

    public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(secret));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var identifier = Guid.NewGuid().ToString("N");
        var reference = SecretReference.Create(identifier);
        var target = CredentialManagerTarget.ForReference(reference);

        EnsureWithinCredentialLimit(reference, secret);

        // The marker goes first so that a crash can never leave a credential whose reference still
        // reads the legacy file.
        var markerCreated = EnsureAuthorityMarker(identifier, reference);
        var write = _credentials.Write(
            target,
            secret,
            CredentialManagerTarget.UserName,
            CredentialManagerTarget.Comment);

        if (write.IsSuccess)
        {
            RecordWrite(reference, CredentialManagerOutcome.Success, markerCreated);
            return reference;
        }

        // A marker that outlives a failed write would suppress the only copy of the value.
        if (markerCreated && !_markers.TryRemove(identifier))
        {
            RecordWrite(reference, write.Outcome, markerCreated);
            throw new SecretStorageException(SecretStorageFailureReason.AuthorityMarkerUnchanged, reference);
        }

        if (write.Outcome is CredentialManagerOutcome.NotSupported or CredentialManagerOutcome.ApiUnavailable)
        {
            // Classified unavailability on a reference that was just created: the value cannot have
            // existed in the Credential Manager before, so the legacy layout is the whole payload.
            RecordWrite(reference, write.Outcome, markerCreated: false);
            await _legacy.WriteSecretPayloadAsync(identifier, secret, cancellationToken).ConfigureAwait(false);
            return reference;
        }

        RecordWrite(reference, write.Outcome, markerCreated: false);
        throw new SecretStorageException(MapReason(write.Outcome), reference);
    }

    public async Task<string?> GetSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identifier = SecretReference.GetIdentifier(secretReference);
        var target = CredentialManagerTarget.ForReference(secretReference);
        var read = _credentials.Read(target);

        if (read.IsSuccess)
        {
            // A corrupt credential is a failure, never a hint that the legacy file might still work.
            return DecodeCredentialValue(secretReference, read.Blob);
        }

        if (read.Outcome == CredentialManagerOutcome.NotFound)
        {
            if (_markers.Exists(identifier))
            {
                RecordRead(secretReference, read.Outcome);
                return null;
            }

            RecordRead(secretReference, read.Outcome);
            return await _legacy.GetSecretAsync(secretReference, cancellationToken).ConfigureAwait(false);
        }

        RecordRead(secretReference, read.Outcome);
        throw new SecretStorageException(MapReason(read.Outcome), secretReference);
    }

    public Task<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identifier = SecretReference.GetIdentifier(secretReference);
        var target = CredentialManagerTarget.ForReference(secretReference);

        // The legacy file goes first. A deletion that fails here must not remove the credential,
        // because the reference would then be gone while its authority marker still claims it.
        var legacyRemoved = _legacy.DeleteSecretAsync(secretReference, cancellationToken).GetAwaiter().GetResult();

        var delete = _credentials.Delete(target);

        switch (delete.Outcome)
        {
            case CredentialManagerOutcome.Success:
                // Both representations are gone, so the marker may go with them.
                _markers.TryRemove(identifier);
                RecordWrite(secretReference, delete.Outcome, markerCreated: false);
                return Task.FromResult(true);

            case CredentialManagerOutcome.NotFound:
                // The credential is already absent, which is a normal state and not an error.
                _markers.TryRemove(identifier);
                RecordWrite(secretReference, delete.Outcome, markerCreated: false);
                return Task.FromResult(legacyRemoved);

            default:
                // The payload is never recreated, and a surviving credential stays authoritative
                // behind its marker, so no value is lost and none is resurrected.
                RecordWrite(secretReference, delete.Outcome, markerCreated: false);
                throw new SecretStorageException(MapReason(delete.Outcome), secretReference);
        }
    }

    /// <summary>
    /// Reports whether the value behind an existing reference can still be resolved. A store or API
    /// failure is reported as a non-usable probe rather than as an exception, so route selection keeps
    /// working and a failure can never become an unauthenticated request (ADR-0005 §5.2). Cancellation
    /// still propagates.
    /// </summary>
    public async Task<bool> PayloadExistsAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbePayloadAsync(secretReference, cancellationToken).ConfigureAwait(false);
        return probe.IsPresent;
    }

    /// <summary>
    /// The read-only form of <see cref="PayloadExistsAsync"/> that also reports why a reference is not
    /// present, using the closed reason codes only.
    /// </summary>
    public async Task<SecretPayloadProbe> ProbePayloadAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identifier = SecretReference.GetIdentifier(secretReference);
        var read = _credentials.Read(CredentialManagerTarget.ForReference(secretReference));

        try
        {
            if (read.IsSuccess)
            {
                // A credential whose DPAPI file was removed is still present, and a corrupt one is
                // present but not usable.
                return TryDecode(read.Blob, out _)
                    ? SecretPayloadProbe.Present()
                    : SecretPayloadProbe.Unavailable(SecretStorageFailureReason.CredentialManagerRecordCorrupt);
            }

            if (read.Outcome == CredentialManagerOutcome.NotFound)
            {
                if (_markers.Exists(identifier))
                {
                    return SecretPayloadProbe.Absent();
                }

                return await _legacy.ProbePayloadAsync(secretReference, cancellationToken).ConfigureAwait(false);
            }

            return SecretPayloadProbe.Unavailable(MapReason(read.Outcome));
        }
        finally
        {
            ClearBlob(read.Blob);
        }
    }

    /// <summary>
    /// Overwrites the value behind an existing reference. The Credential Manager is written first and
    /// the legacy file is only removed after that succeeded, so a failed rotation leaves the previous
    /// value readable both as a credential and as a file.
    /// </summary>
    public async Task OverwriteSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret value must not be empty.", nameof(secret));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var identifier = SecretReference.GetIdentifier(secretReference);
        var target = CredentialManagerTarget.ForReference(secretReference);

        EnsureWithinCredentialLimit(secretReference, secret);

        var read = _credentials.Read(target);
        var outcome = read.Outcome;

        try
        {
            if (outcome == CredentialManagerOutcome.Success)
            {
                if (!TryDecode(read.Blob, out _))
                {
                    throw new SecretStorageException(
                        SecretStorageFailureReason.CredentialManagerRecordCorrupt,
                        secretReference);
                }
            }
            else if (outcome != CredentialManagerOutcome.NotFound)
            {
                // Absence is unproved. Writing the legacy layout now could let a later successful read
                // hide the newer DPAPI value behind an older credential, so the rotation fails instead.
                throw new SecretStorageException(MapReason(outcome), secretReference);
            }
        }
        finally
        {
            ClearBlob(read.Blob);
        }

        var credentialExisted = outcome == CredentialManagerOutcome.Success;
        var markerCreated = EnsureAuthorityMarker(identifier, secretReference);

        var write = _credentials.Write(
            target,
            secret,
            CredentialManagerTarget.UserName,
            CredentialManagerTarget.Comment);

        if (write.IsSuccess)
        {
            // The credential is authoritative from here on, so a stale legacy file is removed
            // best-effort; a deletion that fails is retried on a later overwrite or delete.
            _legacy.TryDeleteSecretPayload(identifier);
            RecordWrite(secretReference, write.Outcome, markerCreated);
            return;
        }

        if (markerCreated && !_markers.TryRemove(identifier))
        {
            RecordWrite(secretReference, write.Outcome, markerCreated);
            throw new SecretStorageException(SecretStorageFailureReason.AuthorityMarkerUnchanged, secretReference);
        }

        if (!credentialExisted
            && markerCreated
            && write.Outcome is CredentialManagerOutcome.NotSupported or CredentialManagerOutcome.ApiUnavailable)
        {
            // Classified unavailability with a marker this call created: the marker is gone again, so
            // the legacy write below is a plain legacy payload with no Credential Manager authority.
            RecordWrite(secretReference, write.Outcome, markerCreated: false);
            await _legacy.WriteSecretPayloadAsync(identifier, secret, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A marker that already existed means a Credential Manager value may have existed for this
        // reference, so the legacy layout is not a valid substitute.
        RecordWrite(secretReference, write.Outcome, markerCreated: false);
        throw new SecretStorageException(MapReason(write.Outcome), secretReference);
    }

    /// <summary>
    /// Returns true when the marker was created by this call. An existing marker is never removed by
    /// a failed write, because it records that a Credential Manager value may have been stored before.
    /// </summary>
    private bool EnsureAuthorityMarker(string identifier, string secretReference)
    {
        if (_markers.Exists(identifier))
        {
            return false;
        }

        // Deliberately not cancellable: a cancellation between the marker and the credential would
        // leave a legacy secret unreadable for no benefit, because the write has to be attempted
        // anyway to know whether the marker is needed.
        _markers.CreateAsync(identifier, secretReference, CancellationToken.None).GetAwaiter().GetResult();
        return true;
    }

    private static string DecodeCredentialValue(string secretReference, byte[]? blob)
    {
        try
        {
            if (!TryDecode(blob, out var value))
            {
                throw new SecretStorageException(
                    SecretStorageFailureReason.CredentialManagerRecordCorrupt,
                    secretReference);
            }

            return value;
        }
        finally
        {
            ClearBlob(blob);
        }
    }

    private static bool TryDecode(byte[]? blob, [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (blob is null || blob.Length == 0)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(blob);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (value.IndexOf('\0') >= 0)
        {
            // A managed payload is exactly the UTF-8 bytes of the value, so an embedded terminator
            // means the record was not written by this application.
            value = null;
            return false;
        }

        return true;
    }

    private static void EnsureWithinCredentialLimit(string secretReference, string secret)
    {
        if (Encoding.UTF8.GetByteCount(secret) > ICredentialManagerApi.MaxCredentialBlobSize)
        {
            throw new SecretStorageException(
                SecretStorageFailureReason.CredentialManagerRecordRejected,
                secretReference);
        }
    }

    private static SecretStorageFailureReason MapReason(CredentialManagerOutcome outcome) => outcome switch
    {
        CredentialManagerOutcome.ApiUnavailable =>
            SecretStorageFailureReason.CredentialManagerApiUnavailable,
        CredentialManagerOutcome.NotSupported =>
            SecretStorageFailureReason.CredentialManagerNotSupported,
        CredentialManagerOutcome.AccessDenied =>
            SecretStorageFailureReason.CredentialManagerAccessDenied,
        CredentialManagerOutcome.NoLogonSession =>
            SecretStorageFailureReason.CredentialManagerNoLogonSession,
        CredentialManagerOutcome.InvalidParameter =>
            SecretStorageFailureReason.CredentialManagerRecordRejected,
        _ => SecretStorageFailureReason.CredentialManagerOperationFailed
    };

    private static void ClearBlob(byte[]? blob)
    {
        if (blob is { Length: > 0 })
        {
            Array.Clear(blob);
        }
    }

    /// <summary>
    /// Records a write decision as a closed reason code plus the URN, which is all this application is
    /// allowed to write about a backend choice (ADR-0005 §4). Read precedence and the authority
    /// marker remain the source of truth after a crash, not this record.
    /// </summary>
    private void RecordWrite(string secretReference, CredentialManagerOutcome outcome, bool markerCreated)
    {
        _logger?.LogInformation(
            "Secret reference {SecretReference} was written through the credential manager boundary: {CredentialManagerOutcome}, marker created {AuthorityMarkerCreated}.",
            secretReference,
            outcome,
            markerCreated);
    }

    /// <summary>Records a read decision, again as a closed reason code plus the URN only.</summary>
    private void RecordRead(string secretReference, CredentialManagerOutcome outcome)
    {
        _logger?.LogDebug(
            "Secret reference {SecretReference} was read through the credential manager boundary: {CredentialManagerOutcome}.",
            secretReference,
            outcome);
    }
}

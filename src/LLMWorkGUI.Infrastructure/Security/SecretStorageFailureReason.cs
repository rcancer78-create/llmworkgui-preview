namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// The closed set of reasons a secret payload could not be stored or resolved. It is the only thing
/// this application records about a backend decision: the URN plus one of these codes, never the
/// value, never the path, never a native message that could contain either (ADR-0005 §4).
/// </summary>
public enum SecretStorageFailureReason
{
    /// <summary>No failure. Used by a successful probe.</summary>
    None = 0,

    /// <summary>The Credential Manager entry points could not be loaded, so no value could be stored there.</summary>
    CredentialManagerApiUnavailable,

    /// <summary>
    /// Credential Manager answered <c>ERROR_NOT_SUPPORTED</c> (50) where a fallback was not allowed
    /// because a Credential Manager value may have existed for the reference.
    /// </summary>
    CredentialManagerNotSupported,

    /// <summary>Credential Manager answered <c>ERROR_ACCESS_DENIED</c> (5).</summary>
    CredentialManagerAccessDenied,

    /// <summary>Credential Manager answered <c>ERROR_NO_SUCH_LOGON_SESSION</c> (1312).</summary>
    CredentialManagerNoLogonSession,

    /// <summary>
    /// Credential Manager answered <c>ERROR_INVALID_PARAMETER</c> (87), which is also what Windows
    /// reports for a payload larger than <see cref="ICredentialManagerApi.MaxCredentialBlobSize"/>.
    /// A secret that used to fit the DPAPI payload may therefore become unsavable.
    /// </summary>
    CredentialManagerRecordRejected,

    /// <summary>An unclassified Win32 failure. Absence of the credential is not proved by it.</summary>
    CredentialManagerOperationFailed,

    /// <summary>
    /// The credential exists but its blob is empty, is not strict UTF-8, or carries an extra
    /// terminator. The legacy payload is not consulted: a corrupt record is a failure, not a hint
    /// that something else might still be readable.
    /// </summary>
    CredentialManagerRecordCorrupt,

    /// <summary>
    /// A newly created authority marker could not be removed after its <c>CredWrite</c> failed. The
    /// legacy path is deliberately not taken in that state, because the marker would suppress the only
    /// copy of the value.
    /// </summary>
    AuthorityMarkerUnchanged,

    /// <summary>The legacy DPAPI record is malformed, undecodable or cannot be decrypted.</summary>
    LegacyPayloadCorrupt,

    /// <summary>The legacy record could not be read; this does not establish absence.</summary>
    LegacyPayloadUnavailable
}

/// <summary>
/// Raised when a secret payload cannot be stored or resolved for a reason that must not degrade into
/// a fallback. The message carries the URN and the reason code only.
/// </summary>
public sealed class SecretStorageException : InvalidOperationException
{
    public SecretStorageException(SecretStorageFailureReason reason, string secretReference)
        : base($"The secret payload for reference '{secretReference}' could not be used: {Describe(reason)} (reason code {reason}).")
    {
        Reason = reason;
        SecretReference = secretReference;
    }

    public SecretStorageFailureReason Reason { get; }

    public string SecretReference { get; }

    private static string Describe(SecretStorageFailureReason reason) => reason switch
    {
        SecretStorageFailureReason.CredentialManagerApiUnavailable =>
            "the Windows Credential Manager is not available in this environment",
        SecretStorageFailureReason.CredentialManagerNotSupported =>
            "the Windows Credential Manager refused the operation and no fallback is allowed for this reference",
        SecretStorageFailureReason.CredentialManagerAccessDenied =>
            "access to the Windows Credential Manager was denied",
        SecretStorageFailureReason.CredentialManagerNoLogonSession =>
            "no Windows logon session is available for the credential profile",
        SecretStorageFailureReason.CredentialManagerRecordRejected =>
            $"the value was rejected by the Windows Credential Manager, including a payload larger than {ICredentialManagerApi.MaxCredentialBlobSize} bytes",
        SecretStorageFailureReason.CredentialManagerOperationFailed =>
            "the Windows Credential Manager operation failed for an unclassified reason",
        SecretStorageFailureReason.CredentialManagerRecordCorrupt =>
            "the stored credential record is corrupt and cannot be decoded",
        SecretStorageFailureReason.AuthorityMarkerUnchanged =>
            "the credential authority marker could not be restored, so the operation failed closed",
        SecretStorageFailureReason.LegacyPayloadCorrupt =>
            "the legacy credential record is corrupt or cannot be decrypted",
        SecretStorageFailureReason.LegacyPayloadUnavailable =>
            "the legacy credential record cannot be read",
        _ => "the secret payload is not usable"
    };
}

/// <summary>What a read-only payload probe concluded. <see cref="Unavailable"/> never means "no value".</summary>
public enum SecretPayloadPresence
{
    /// <summary>The probe resolved a usable value.</summary>
    Present = 0,

    /// <summary>No representation of the payload exists. The reference is <c>Missing</c>.</summary>
    Absent,

    /// <summary>
    /// The probe could not be completed, so the payload must be treated as unusable. An exception is
    /// not raised, because route selection must not fail on a transient store error, and it must not
    /// degrade into an unauthenticated request either.
    /// </summary>
    Unavailable
}

/// <summary>
/// The outcome of a read-only payload probe. It carries no value, so it is safe to pass to routing,
/// probes and the UI.
/// </summary>
public readonly record struct SecretPayloadProbe(
    SecretPayloadPresence Presence,
    SecretStorageFailureReason Reason = SecretStorageFailureReason.None)
{
    public bool IsPresent => Presence == SecretPayloadPresence.Present;

    public static SecretPayloadProbe Present() =>
        new(SecretPayloadPresence.Present, SecretStorageFailureReason.None);

    public static SecretPayloadProbe Absent() =>
        new(SecretPayloadPresence.Absent, SecretStorageFailureReason.None);

    public static SecretPayloadProbe Unavailable(SecretStorageFailureReason reason) =>
        new(SecretPayloadPresence.Unavailable, reason);
}

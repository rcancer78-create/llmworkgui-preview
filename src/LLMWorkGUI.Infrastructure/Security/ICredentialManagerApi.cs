using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// Closed classification of one native Credential Manager call. The set is deliberately closed: the
/// store may only fall back to the legacy DPAPI payload for the two outcomes that prove the API
/// cannot store a value right now, and every other failure has to fail closed (ADR-0005 §1.1, §1.3).
/// </summary>
public enum CredentialManagerOutcome
{
    /// <summary>The call succeeded.</summary>
    Success = 0,

    /// <summary><c>ERROR_NOT_FOUND</c> (1168): no credential exists for the target.</summary>
    NotFound,

    /// <summary><c>ERROR_NOT_SUPPORTED</c> (50): the credential manager refused the operation.</summary>
    NotSupported,

    /// <summary><c>ERROR_ACCESS_DENIED</c> (5).</summary>
    AccessDenied,

    /// <summary><c>ERROR_NO_SUCH_LOGON_SESSION</c> (1312): the user profile is not loaded.</summary>
    NoLogonSession,

    /// <summary>
    /// <c>ERROR_INVALID_PARAMETER</c> (87), which is also what Windows reports for a credential blob
    /// larger than <see cref="MaxCredentialBlobSize"/>.
    /// </summary>
    InvalidParameter,

    /// <summary>
    /// The Credential Manager entry points could not be loaded at all. This is the only failure that
    /// is equivalent to <see cref="NotSupported"/> for the purposes of the DPAPI fallback.
    /// </summary>
    ApiUnavailable,

    /// <summary>Any other Win32 failure, including one this application does not classify.</summary>
    Failed
}

/// <summary>
/// The result of a Credential Manager read. <see cref="Blob"/> is only populated on
/// <see cref="CredentialManagerOutcome.Success"/> and is owned by the caller, which must clear it as
/// soon as the value has been decoded.
/// </summary>
public sealed class CredentialManagerReadResult
{
    private CredentialManagerReadResult(CredentialManagerOutcome outcome, byte[]? blob)
    {
        Outcome = outcome;
        Blob = blob;
    }

    public CredentialManagerOutcome Outcome { get; }

    public byte[]? Blob { get; }

    public bool IsSuccess => Outcome == CredentialManagerOutcome.Success;

    public static CredentialManagerReadResult Success(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        return new CredentialManagerReadResult(CredentialManagerOutcome.Success, blob);
    }

    public static CredentialManagerReadResult Failure(CredentialManagerOutcome outcome)
    {
        if (outcome == CredentialManagerOutcome.Success)
        {
            throw new ArgumentException("A failed read cannot carry the success outcome.", nameof(outcome));
        }

        return new CredentialManagerReadResult(outcome, blob: null);
    }
}

/// <summary>The classified result of a Credential Manager write or delete.</summary>
public readonly record struct CredentialManagerOperationResult(CredentialManagerOutcome Outcome)
{
    public bool IsSuccess => Outcome == CredentialManagerOutcome.Success;

    public static CredentialManagerOperationResult Success() => new(CredentialManagerOutcome.Success);

    public static CredentialManagerOperationResult Failure(CredentialManagerOutcome outcome) => new(outcome);
}

/// <summary>
/// The narrow, testable boundary to the Win32 Credential Manager. Everything above it is pure policy:
/// which failure may fall back to the legacy DPAPI payload, when the authority marker exists, and in
/// which order a read, a write and a delete touch the two representations of a payload.
/// </summary>
public interface ICredentialManagerApi
{
    /// <summary>
    /// <c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>. Windows rejects a generic credential whose blob exceeds
    /// this with <c>ERROR_INVALID_PARAMETER</c>, so the size is checked before a marker is created.
    /// </summary>
    const int MaxCredentialBlobSize = 2560;

    /// <summary>Reads the generic credential stored at <paramref name="target"/>.</summary>
    CredentialManagerReadResult Read(string target);

    /// <summary>
    /// Writes (or replaces) the generic credential at <paramref name="target"/>. The value is encoded
    /// as strict UTF-8 with no terminating null.
    /// </summary>
    CredentialManagerOperationResult Write(string target, string secret, string userName, string comment);

    /// <summary>Deletes the generic credential stored at <paramref name="target"/>.</summary>
    CredentialManagerOperationResult Delete(string target);
}

/// <summary>
/// The deterministic Credential Manager target of a secret reference (ADR-0005 §1.2). The target is
/// derived from the URN alone, so a reference shared by a provider profile and an account, and a
/// reference that nothing is bound to, all address the same single credential. Neither the target,
/// the user name nor the comment may ever contain a secret value.
/// </summary>
public static class CredentialManagerTarget
{
    public const string Prefix = "LLMWorkGUI/secret/";

    /// <summary>User name of a managed generic credential. Reference-free and value-free by contract.</summary>
    public const string UserName = "LLMWorkGUI";

    /// <summary>Comment of a managed generic credential. Reference-free and value-free by contract.</summary>
    public const string Comment = "LLMWorkGUI secret payload";

    /// <summary>
    /// Returns the target of <paramref name="secretReference"/>, rejecting a malformed reference with
    /// the same <see cref="ArgumentException"/> the URN contract already uses.
    /// </summary>
    public static string ForReference(string secretReference) =>
        Prefix + SecretReference.GetIdentifier(secretReference);
}

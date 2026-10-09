namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Lifecycle state of a secret reference (ADR-0005 §2.4, §5.2). The value itself never appears in
/// the database: only the URN and this state do, so a database dump stays safe by construction.
/// </summary>
public enum SecretReferenceState
{
    /// <summary>The reference is registered and its payload is expected to be present.</summary>
    Active,

    /// <summary>
    /// The reference is known but its payload cannot be resolved: the file is gone, was never
    /// created, or cannot be unprotected for the current Windows profile. Re-entering the value is
    /// required, which is an expected state after a database restore and not a migration failure
    /// (ADR-0005 §5.3).
    /// </summary>
    Missing,

    /// <summary>
    /// The secret was deleted through the application. The URN stays in the database so dependent
    /// routes can be reported as ineligible instead of silently losing their credential, and it can
    /// never be used again without a new reference (ADR-0005 §5.2).
    /// </summary>
    Revoked
}

/// <summary>What a stored secret is used for. Recorded so a URN is never resolved for the wrong purpose.</summary>
public enum SecretReferenceKind
{
    /// <summary>
    /// The purpose could not be established, for example for a reference that predates this metadata.
    /// Payload availability alone does not authorize an egress purpose. Provider HTTP requests
    /// reject this kind; explicitly saving a new API key creates a correctly typed reference.
    /// </summary>
    Unspecified,

    /// <summary>An API key of a user-configured OpenAI-compatible provider.</summary>
    ProviderApiKey,

    /// <summary>A dedicated key authorizing local NativeGateway HTTP requests.</summary>
    GatewayApiKey,

    /// <summary>A secret HTTP header of a user-configured provider.</summary>
    ProviderHeader
}

/// <summary>
/// Which kind of entity a secret reference is bound to. A single URN may be bound to several owners,
/// so this is recorded per binding and never as a property of the reference itself.
/// </summary>
public enum SecretReferenceOwnerKind
{
    /// <summary>The owner could not be established; the binding is not used for cleanup decisions.</summary>
    Unknown,

    /// <summary>The URN is referenced by <c>ProviderProfiles.ApiKeySecretReference</c>.</summary>
    ProviderProfile,

    /// <summary>The URN is referenced by <c>Accounts.SecretReference</c>.</summary>
    Account
}

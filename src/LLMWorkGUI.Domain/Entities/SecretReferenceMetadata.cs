using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

/// <summary>
/// Reference-only metadata for one secret URN (ADR-0005 §2.1). The secret value is never a member:
/// it lives only in the Windows-protected payload the URN points to.
/// </summary>
public sealed record SecretReferenceMetadata
{
    public required string Reference { get; init; }

    public required SecretReferenceKind Kind { get; init; }

    /// <summary>The state recorded by the last lifecycle operation, not a live probe of the payload.</summary>
    public required SecretReferenceState State { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the value behind this URN was last overwritten, or <c>null</c> when it never was.</summary>
    public DateTimeOffset? LastRotatedAtUtc { get; init; }

    /// <summary>When the secret was revoked, or <c>null</c> while it is not revoked.</summary>
    public DateTimeOffset? RevokedAtUtc { get; init; }

    public bool IsRevoked => State == SecretReferenceState.Revoked;
}

/// <summary>
/// One owner a secret reference is bound to. Several owners may share a single URN, so bindings are
/// stored separately from the reference and are not unique per reference.
/// </summary>
public sealed record SecretReferenceOwnerBinding
{
    public required string Reference { get; init; }

    public required SecretReferenceOwnerKind OwnerKind { get; init; }

    public required string OwnerId { get; init; }

    public static SecretReferenceOwnerBinding ForProviderProfile(string reference, string providerProfileId) =>
        new()
        {
            Reference = reference,
            OwnerKind = SecretReferenceOwnerKind.ProviderProfile,
            OwnerId = providerProfileId
        };

    public static SecretReferenceOwnerBinding ForAccount(string reference, string accountId) =>
        new()
        {
            Reference = reference,
            OwnerKind = SecretReferenceOwnerKind.Account,
            OwnerId = accountId
        };
}

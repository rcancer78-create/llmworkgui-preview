using System.Globalization;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

/// <summary>
/// The answer to "may this URN be used, and what is known about it?" (ADR-0005 §2.4). Only the URN
/// and non-sensitive metadata are exposed; the secret value is never part of a status.
/// </summary>
public sealed record SecretReferenceStatus
{
    public required string Reference { get; init; }

    /// <summary>
    /// The effective state, which combines the recorded state with whether the payload is actually
    /// present. A recorded <see cref="SecretReferenceState.Active"/> whose payload is gone is
    /// reported as <see cref="SecretReferenceState.Missing"/> instead of being trusted.
    /// </summary>
    public required SecretReferenceState State { get; init; }

    public SecretReferenceKind Kind { get; init; } = SecretReferenceKind.Unspecified;

    /// <summary>False for a reference that predates the reference-metadata table.</summary>
    public bool IsRegistered { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? LastRotatedAtUtc { get; init; }

    /// <summary>
    /// True only when the value behind the URN can be resolved. Routing, probes and connection
    /// tests must refuse to send anything when this is false.
    /// </summary>
    public bool IsUsable => State == SecretReferenceState.Active;

    /// <summary>Redacted, non-technical summary for the UI and for refusal messages.</summary>
    public string DescribeState()
    {
        switch (State)
        {
            case SecretReferenceState.Active:
                return LastRotatedAtUtc is null
                    ? "secret is set"
                    : "secret is set (rotated " + LastRotatedAtUtc.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";

            case SecretReferenceState.Missing:
                return "secret is missing: the stored value can no longer be read and must be entered again";

            case SecretReferenceState.Revoked:
                return "secret was revoked and can no longer be used; enter the value again to create a new reference";

            default:
                return "secret state is unknown";
        }
    }

    public static SecretReferenceStatus Active(
        string reference,
        SecretReferenceKind kind,
        bool isRegistered,
        DateTimeOffset? createdAtUtc,
        DateTimeOffset? lastRotatedAtUtc) =>
        new()
        {
            Reference = reference,
            State = SecretReferenceState.Active,
            Kind = kind,
            IsRegistered = isRegistered,
            CreatedAtUtc = createdAtUtc,
            LastRotatedAtUtc = lastRotatedAtUtc
        };

    public static SecretReferenceStatus Missing(
        string reference,
        SecretReferenceKind kind = SecretReferenceKind.Unspecified,
        bool isRegistered = false) =>
        new()
        {
            Reference = reference,
            State = SecretReferenceState.Missing,
            Kind = kind,
            IsRegistered = isRegistered
        };

    public static SecretReferenceStatus Revoked(
        string reference,
        SecretReferenceKind kind = SecretReferenceKind.Unspecified,
        bool isRegistered = false,
        DateTimeOffset? createdAtUtc = null) =>
        new()
        {
            Reference = reference,
            State = SecretReferenceState.Revoked,
            Kind = kind,
            IsRegistered = isRegistered,
            CreatedAtUtc = createdAtUtc
        };
}

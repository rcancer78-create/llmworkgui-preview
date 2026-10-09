using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class ApprovalRule
{
    public ApprovalRule(
        string id,
        BackendType backend,
        string providerProfileId,
        string projectId,
        string? pathScope,
        NormalizedApprovalKind kind,
        string operation,
        DateTimeOffset? expiresAt,
        string createdBy,
        DateTimeOffset createdAt)
    {
        if (kind == NormalizedApprovalKind.UnknownHighRisk)
        {
            throw new ArgumentException("UnknownHighRisk never receives a persistent approval rule.", nameof(kind));
        }

        Id = DomainGuard.NotBlank(id, nameof(id));
        Backend = backend;
        ProviderProfileId = DomainGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        ProjectId = DomainGuard.NotBlank(projectId, nameof(projectId));
        PathScope = DomainGuard.OptionalNotBlank(pathScope, nameof(pathScope));
        Kind = kind;
        Operation = DomainGuard.NotBlank(operation, nameof(operation));
        ExpiresAt = expiresAt;
        CreatedBy = DomainGuard.NotBlank(createdBy, nameof(createdBy));
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public BackendType Backend { get; }

    public string ProviderProfileId { get; }

    public string ProjectId { get; }

    public string? PathScope { get; }

    public NormalizedApprovalKind Kind { get; }

    public string Operation { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public string CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}

using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class WorkflowVersion
{
    public WorkflowVersion(
        string id,
        string workflowPackageId,
        int versionNumber,
        string blobId,
        string originalHash,
        WorkflowSourceType sourceType,
        string? entrypointsJson,
        string? declaredRolesJson,
        string? bindingsJson,
        string? compatibilityReportJson,
        string? creationMetadataJson,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? activatedAtUtc)
    {
        if (versionNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionNumber),
                "Workflow version number must be at least 1.");
        }

        Id = DomainGuard.NotBlank(id, nameof(id));
        WorkflowPackageId = DomainGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        VersionNumber = versionNumber;
        BlobId = WorkflowBlobIdGuard.NotBlankBlobId(blobId, nameof(blobId));
        OriginalHash = WorkflowBlobIdGuard.NotBlankBlobId(originalHash, nameof(originalHash));
        SourceType = sourceType;
        EntrypointsJson = DomainGuard.OptionalNotBlank(entrypointsJson, nameof(entrypointsJson));
        DeclaredRolesJson = DomainGuard.OptionalNotBlank(declaredRolesJson, nameof(declaredRolesJson));
        BindingsJson = DomainGuard.OptionalNotBlank(bindingsJson, nameof(bindingsJson));
        CompatibilityReportJson = DomainGuard.OptionalNotBlank(compatibilityReportJson, nameof(compatibilityReportJson));
        CreationMetadataJson = DomainGuard.OptionalNotBlank(creationMetadataJson, nameof(creationMetadataJson));
        CreatedAtUtc = createdAtUtc;
        ActivatedAtUtc = activatedAtUtc;
    }

    public string Id { get; }

    public string WorkflowPackageId { get; }

    public int VersionNumber { get; }

    public string BlobId { get; }

    public string OriginalHash { get; }

    public WorkflowSourceType SourceType { get; }

    public string? EntrypointsJson { get; }

    public string? DeclaredRolesJson { get; }

    public string? BindingsJson { get; }

    public string? CompatibilityReportJson { get; }

    public string? CreationMetadataJson { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ActivatedAtUtc { get; }
}

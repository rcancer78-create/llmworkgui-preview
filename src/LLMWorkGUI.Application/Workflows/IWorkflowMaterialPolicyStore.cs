using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>Local operator classification bound to immutable version bytes; not native consent or identity.</summary>
public sealed record WorkflowMaterialPolicy(string VersionId, string BlobId, DataClassification Classification,
    long Revision, bool IsDeclared);

public interface IWorkflowMaterialPolicyStore
{
    /// <summary>An undeclared existing version returns Restricted, revision zero; it never becomes Public by default.</summary>
    Task<WorkflowMaterialPolicy> ReadAsync(string versionId, CancellationToken cancellationToken = default);
    Task<WorkflowMaterialPolicy> DeclareAsync(string versionId, string expectedBlobId, long expectedRevision,
        DataClassification classification, CancellationToken cancellationToken = default);
}

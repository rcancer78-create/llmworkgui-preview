using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Declarative;

/// <summary>
/// Captured content explicitly associated with the request's existing execution. The caller owns the
/// stream. No path is interpreted and no model authorship or reviewer authority is inferred from it.
/// </summary>
public sealed record WorkflowArtifactCollectionInput(string RunId, Stream Content, DataClassification Classification);

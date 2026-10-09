using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>The exact committed row and the updated run snapshot, independent of artifact list ordering.</summary>
public sealed record WorkflowArtifactRecordingResult(WorkflowRun Run, WorkflowArtifactEvidence Artifact);

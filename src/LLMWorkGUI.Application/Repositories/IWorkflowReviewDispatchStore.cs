using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Workflows.Declarative;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>Atomic persistence for one reviewer role; separate roles may have separate outcomes.</summary>
public interface IWorkflowReviewDispatchStore
{
    /// <summary>Writes all three admission rows together, or returns false for a matching active,
    /// successful or uncertain attempt, or a changed/terminal run, stage or artifact.
    /// A retry requires a matching Failed/Cancelled predecessor.</summary>
    Task<bool> TryAdmitAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default);

    /// <summary>Same admission with the exact prompt hash for a project-bound HTTP consumer.
    /// Default preserves synthetic stores; it does not create production transport authority.</summary>
    Task<bool> TryAdmitWithPromptAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        string prompt, CancellationToken cancellationToken = default) => TryAdmitAsync(session, execution, evidence, cancellationToken);

    /// <summary>Commits the session, execution outcome and binding observation together.</summary>
    Task CompleteAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default);

    /// <summary>Commits optional bounded response content in the same transaction as completion. No verdict is inferred.</summary>
    Task CompleteWithResponseAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        WorkflowModelResponse? response, CancellationToken cancellationToken = default) => response is null
            ? CompleteAsync(session, execution, evidence, cancellationToken)
            : throw new NotSupportedException("This store cannot atomically persist reviewer response evidence.");
}

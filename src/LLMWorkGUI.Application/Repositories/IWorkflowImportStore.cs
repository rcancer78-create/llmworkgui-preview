using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>Commits a package and its first version atomically, reusing an existing original hash.</summary>
public interface IWorkflowImportStore
{
    Task<WorkflowImportResult> CommitImportAsync(WorkflowPackage package, WorkflowVersion firstVersion,
        CancellationToken cancellationToken = default);
}

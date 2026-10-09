using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Concurrency;

public interface ICheckoutLockService
{
    bool RequiresWriterLock(string? executionMode);

    bool RequiresWriterLock(WorkflowRole role, string? executionMode);

    Task<ICheckoutLockToken> AcquireWriterLockAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        CancellationToken cancellationToken = default);

    Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        string? executionMode,
        WorkflowRole role = WorkflowRole.Unknown,
        CancellationToken cancellationToken = default);
}

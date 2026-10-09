using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Concurrency;

public sealed class CheckoutLockService : ICheckoutLockService
{
    private static readonly HashSet<string> ReadOnlyModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "plan",
        "ask",
        "diff",
        "review"
    };

    private readonly IProjectLockRepository _projectLockRepository;
    private readonly IApplicationInstanceGuard _instanceGuard;
    private readonly TimeProvider _timeProvider;

    public CheckoutLockService(
        IProjectLockRepository projectLockRepository,
        IApplicationInstanceGuard instanceGuard,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(projectLockRepository);
        ArgumentNullException.ThrowIfNull(instanceGuard);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _projectLockRepository = projectLockRepository;
        _instanceGuard = instanceGuard;
        _timeProvider = timeProvider;
    }

    public bool RequiresWriterLock(string? executionMode)
    {
        if (string.IsNullOrWhiteSpace(executionMode))
        {
            return true;
        }

        return !ReadOnlyModes.Contains(executionMode.Trim());
    }

    public bool RequiresWriterLock(WorkflowRole role, string? executionMode)
    {
        return RequiresWriterLock(executionMode);
    }

    public async Task<ICheckoutLockToken> AcquireWriterLockAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        _instanceGuard.EnsureSupervisorPermitted();

        var canonicalRoot = ProjectLock.CanonicalizeRoot(canonicalRootPath);
        var mutexScope = NamedMutexScope.TryAcquire(NamedMutexNames.ForCheckout(canonicalRoot))
            ?? throw new ProjectLockConflictException(
                $"Checkout '{canonicalRoot}' is locked by another application instance.");

        try
        {
            var projectLock = ProjectLock.Acquire(
                Guid.NewGuid().ToString("D"),
                projectId,
                canonicalRoot,
                executionId,
                _instanceGuard.InstanceId,
                processGeneration,
                _timeProvider.GetUtcNow());

            var acquired = await _projectLockRepository
                .TryAcquireAsync(projectLock, cancellationToken)
                .ConfigureAwait(false);

            if (!acquired)
            {
                throw new ProjectLockConflictException(
                    $"Checkout '{canonicalRoot}' already has an active writer lock owned by another execution.");
            }

            return new CheckoutLockToken(projectLock, _projectLockRepository, mutexScope, _timeProvider);
        }
        catch
        {
            mutexScope.Dispose();
            throw;
        }
    }

    public async Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        string? executionMode,
        WorkflowRole role = WorkflowRole.Unknown,
        CancellationToken cancellationToken = default)
    {
        if (!RequiresWriterLock(role, executionMode))
        {
            return null;
        }

        return await AcquireWriterLockAsync(
                projectId,
                canonicalRootPath,
                executionId,
                processGeneration,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

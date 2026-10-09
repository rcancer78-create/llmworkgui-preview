using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Concurrency;

internal sealed class CheckoutLockToken : ICheckoutLockToken
{
    public const string DefaultReleaseReason = "checkout writer lock released";

    private readonly IProjectLockRepository _projectLockRepository;
    private readonly NamedMutexScope _mutexScope;
    private readonly TimeProvider _timeProvider;
    private int _released;
    private readonly SemaphoreSlim _releaseGate = new(1, 1);

    public CheckoutLockToken(
        ProjectLock projectLock,
        IProjectLockRepository projectLockRepository,
        NamedMutexScope mutexScope,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(projectLock);
        ArgumentNullException.ThrowIfNull(projectLockRepository);
        ArgumentNullException.ThrowIfNull(mutexScope);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _projectLockRepository = projectLockRepository;
        _mutexScope = mutexScope;
        _timeProvider = timeProvider;

        LockId = projectLock.Id;
        ProjectId = projectLock.ProjectId;
        CanonicalRootPath = projectLock.CanonicalRootPath;
        ExecutionId = projectLock.ExecutionId;
        ApplicationInstanceId = projectLock.ApplicationInstanceId;
    }

    public string LockId { get; }

    public string ProjectId { get; }

    public string CanonicalRootPath { get; }

    public string ExecutionId { get; }

    public string ApplicationInstanceId { get; }

    public bool IsHeld => Volatile.Read(ref _released) == 0;

    public async Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _releaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _released) == 1)
                return;
            await _projectLockRepository
                .ReleaseAsync(LockId, _timeProvider.GetUtcNow(), reason, cancellationToken)
                .ConfigureAwait(false);
            _mutexScope.Dispose();
            Volatile.Write(ref _released, 1);
        }
        finally
        {
            _releaseGate.Release();
        }
    }

    public void Dispose()
    {
        ReleaseAsync(DefaultReleaseReason).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync(DefaultReleaseReason).ConfigureAwait(false);
    }
}

using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

public sealed class HealthCenterConcurrencyTests
{
    private static readonly HealthScope Scope = HealthScope.ForRoute("concurrent-health-route");

    [Fact]
    public async Task ConcurrentFailures_OnTheSameScope_PreserveBothFailuresAndTripThreshold()
    {
        var states = new PauseFirstWriteRepository();
        var events = new InMemoryHealthEventRepository();
        var service = new HealthCenterService(states, events, new HealthTestTimeProvider(),
            new HealthPolicy { FailureThreshold = 2 });

        var first = service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        await states.FirstWritePaused.WaitAsync(TimeSpan.FromSeconds(5));

        // This repository completes every operation except the first write synchronously. Without
        // service-level serialization the second call therefore reads and writes the stale count
        // before returning its Task; with serialization it waits for the first transition instead.
        var second = service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        states.ReleaseFirstWrite();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = await service.GetSnapshotAsync(Scope);
        Assert.Equal(2, snapshot.AccountedFailureCount);
        Assert.Equal(HealthState.CoolingDown, snapshot.State);
        Assert.False(snapshot.IsRoutable);
        Assert.Equal(new[] { HealthState.Degraded, HealthState.CoolingDown },
            events.Events.Select(entry => entry.NewState));
    }

    [Fact]
    public async Task FailureAlreadyBeingPersisted_ThenManualDisable_DoesNotRestoreRouting()
    {
        var states = new PauseFirstWriteRepository();
        var events = new InMemoryHealthEventRepository();
        var service = new HealthCenterService(states, events, new HealthTestTimeProvider());

        var failure = service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        await states.FirstWritePaused.WaitAsync(TimeSpan.FromSeconds(5));

        var disable = service.DisableManuallyAsync(Scope, "The operator disabled this route.");
        states.ReleaseFirstWrite();
        await Task.WhenAll(failure, disable).WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = await service.GetSnapshotAsync(Scope);
        Assert.Equal(HealthState.DisabledManual, snapshot.State);
        Assert.False(snapshot.IsRoutable);
        Assert.Equal(new[] { HealthState.Degraded, HealthState.DisabledManual },
            events.Events.Select(entry => entry.NewState));
    }

    /// <summary>
    /// Pauses after the service has restored/calculated its first transition but before that write
    /// becomes visible. It permits the exact stale-read/last-write-wins ordering of separate SQLite
    /// Get and Upsert calls, without sleeps, provider I/O, or requiring two reads inside a future lock.
    /// </summary>
    private sealed class PauseFirstWriteRepository : IHealthStateRepository
    {
        private readonly Dictionary<(string Type, string Id), HealthStateRecord> _records = new();
        private readonly object _sync = new();
        private readonly TaskCompletionSource _firstWritePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public Task FirstWritePaused => _firstWritePaused.Task;

        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

        public async Task UpsertAsync(HealthStateRecord record, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _firstWritePaused.TrySetResult();
                await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }

            lock (_sync)
            {
                _records[(record.ScopeType, record.ScopeId)] = record;
            }
        }

        public Task<HealthStateRecord?> GetAsync(string scopeType, string scopeId,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _records.TryGetValue((scopeType, scopeId), out var record);
                return Task.FromResult(record);
            }
        }

        public Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<HealthStateRecord>>(_records.Values.ToArray());
            }
        }
    }
}

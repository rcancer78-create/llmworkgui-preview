using LLMWorkGUI.Application.Security;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.Infrastructure.Mirasim;

/// <summary>Retires local admission and drains actual operations, without claiming native termination.</summary>
public sealed class MirasimLifecycleHostedService : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly object _cancellationGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private TaskCompletionSource? _idle;
    private int _active;
    private bool _stopping;
    private bool _cancellationDisposed;
    private AggregateException? _cancellationFailure;
    private int _disposed;
    private MirasimSessionLifecycleService? _lifecycle;

    public MirasimLifecycleHostedService(IHostApplicationLifetime? lifetime = null)
    {
        if (lifetime is not null) _stoppingRegistration = lifetime.ApplicationStopping.Register(BeginShutdown);
    }

    internal void Attach(MirasimSessionLifecycleService lifecycle)
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, lifecycle, null);
        if (previous is not null && !ReferenceEquals(previous, lifecycle))
            throw new InvalidOperationException("The Mirasim lifetime already has an owner.");
    }

    internal OperationLease BeginOperation(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping) throw new MirasimEgressPolicyException("Mirasim останавливается: новые операции не принимаются.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, _shutdown.Token);
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this, cancellation);
        }
    }

    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken caller,
        Func<T>? stoppedResult = null)
    {
        OperationLease operation;
        try { operation = BeginOperation(caller); }
        catch (MirasimEgressPolicyException) when (stoppedResult is not null) { return Task.FromResult(stoppedResult()); }
        Task<T> task;
        try { task = action(operation.Token); }
        catch (Exception error) { task = Task.FromException<T>(error); }
        // The returned task is already terminal before its continuation releases the drain lease.
        _ = task.ContinueWith(static (_, state) => ((OperationLease)state!).Dispose(), operation,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    internal void BeginShutdown()
    {
        lock (_gate) _stopping = true;
        lock (_cancellationGate)
        {
            if (!_cancellationDisposed && !_shutdown.IsCancellationRequested)
            {
                try { _shutdown.Cancel(); }
                catch (AggregateException error) { _cancellationFailure = error; }
            }
        }
    }

    internal Task IdleTask()
    {
        lock (_gate) return _active == 0 ? Task.CompletedTask : _idle!.Task;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginShutdown();
        await IdleTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _cancellationFailure) is { } failure) throw failure;
        if (Volatile.Read(ref _lifecycle) is { HasUnresolvedOwnership: true })
            throw new InvalidOperationException("Mirasim native completion is unconfirmed; retained ownership requires reconciliation.");
    }

    private void EndOperation()
    {
        TaskCompletionSource? completed = null;
        lock (_gate)
            if (--_active == 0) completed = _idle;
        completed?.TrySetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stoppingRegistration.Dispose();
        BeginShutdown();
        _ = IdleTask().ContinueWith(_ =>
        {
            lock (_cancellationGate)
            {
                _cancellationDisposed = true;
                _shutdown.Dispose();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal sealed class OperationLease(MirasimLifecycleHostedService owner, CancellationTokenSource cancellation) : IDisposable
    {
        private int _disposed;
        public CancellationToken Token => cancellation.Token;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._cancellationGate) cancellation.Dispose();
            owner.EndOperation();
        }
    }
}

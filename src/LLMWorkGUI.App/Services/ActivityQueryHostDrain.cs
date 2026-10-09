using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.App.Services;

/// <summary>Tracks the lazily created query worker without creating a thread just to stop an unused screen.</summary>
public sealed class ActivityQueryLifetime
{
    private readonly object _gate = new();
    private BoundedActivityQueryExecutor? _worker;
    private bool _stopping;

    public void Attach(BoundedActivityQueryExecutor worker)
    {
        lock (_gate)
        {
            if (_worker is not null && !ReferenceEquals(_worker, worker))
                throw new InvalidOperationException("The Activity query lifetime already has a worker.");
            _worker = worker;
            if (_stopping) worker.BeginShutdown();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            return _worker is null ? Task.CompletedTask : _worker.BeginShutdown().WaitAsync(cancellationToken);
        }
    }
}

public sealed class ActivityQueryHostDrain(ActivityQueryLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => lifetime.StopAsync(cancellationToken);
}

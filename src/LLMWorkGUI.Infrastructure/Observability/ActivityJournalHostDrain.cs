using LLMWorkGUI.Application.Observability;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.Infrastructure.Observability;

/// <summary>Retains the actual lazily created journal queue until its physical writes settle.</summary>
public sealed class ActivityJournalLifetime
{
    private readonly object _gate = new();
    private ActivityJournalWriteQueue? _queue;
    private bool _stopping;

    public void Attach(ActivityJournalWriteQueue queue)
    {
        lock (_gate)
        {
            if (_queue is not null && !ReferenceEquals(_queue, queue))
                throw new InvalidOperationException("The Activity journal lifetime already has a queue.");
            _queue = queue;
            if (_stopping) queue.BeginShutdown();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            if (_queue is null) return Task.CompletedTask;
            _queue.BeginShutdown();
            // Cancellation abandons only the host's wait. It does not abort a write or clear its ownership.
            return DrainAsync(_queue, cancellationToken);
        }
    }

    private static async Task DrainAsync(ActivityJournalWriteQueue queue, CancellationToken cancellationToken)
    {
        await queue.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        // A completed reader proves physical work settled, not that every offer reached durable storage.
        // There is no durable loss-recovery receipt yet, so keep the application's interruption marker.
        if (queue.FailedCount != 0 || queue.DroppedCount != 0)
            throw new InvalidOperationException(
                $"Activity journal shutdown is incomplete: {queue.FailedCount} failed writes and {queue.DroppedCount} dropped offers.");
    }
}

public sealed class ActivityJournalHostDrain(ActivityJournalLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => lifetime.StopAsync(cancellationToken);
}

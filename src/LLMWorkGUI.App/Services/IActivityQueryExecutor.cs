namespace LLMWorkGUI.App.Services;

/// <summary>
/// The worker the Activity Center runs its durable queries on.
/// <para>
/// The queries are not a UI concern and must not become one. <c>SqliteActivityEventJournal.QueryPage</c>
/// opens a connection, counts every retained row, counts every match and materializes one page, which at
/// the normative 190 000 rows measured p50 258.5 ms and p95 511.1 ms for a single typed search. That work
/// on the WPF dispatcher is 258 ms of frozen input and rendering per keystroke, and no amount of list
/// reconciliation afterwards makes the window feel responsive.
/// </para>
/// <para>
/// The contract is deliberately small: hand it one unit of query work and it runs it somewhere that is not
/// the UI thread. It is an interface so the coalescing, staleness and disposal behaviour of the screen can
/// be tested headlessly and deterministically, with the query step driven by the test rather than by a
/// racing thread, and so the thread that owns the work is a named, replaceable part instead of an
/// accident of whichever thread happened to raise an event.
/// </para>
/// </summary>
public interface IActivityQueryExecutor
{
    /// <summary>Queues one unit of durable query work. Implementations own and reuse their thread.</summary>
    void Enqueue(Action work);
}

/// <summary>
/// Runs the work on the calling thread.
/// <para>
/// This is the correct default for the headless compositions - the unit tests and the DI-only integration
/// graphs - which have no dispatcher to protect and need their assertions to have run by the time a
/// setter returns. The production shell binds <see cref="BoundedActivityQueryExecutor"/> instead.
/// </para>
/// </summary>
public sealed class InlineActivityQueryExecutor : IActivityQueryExecutor
{
    public static InlineActivityQueryExecutor Instance { get; } = new();

    public void Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        work();
    }
}

/// <summary>
/// The production executor: one background thread that runs queued query work one item at a time.
/// <para>
/// One thread, reused, is the whole point. The obvious alternative - <c>Task.Run</c> per keystroke - costs
/// a pool work item and, once the query touches SQLite, a thread with a live connection and a WAL read
/// transaction on it, for every character the operator types. Because the view model keeps at most one
/// query running and one queued, the queue here is bounded and stays shallow; the bound is enforced rather
/// than assumed, and anything refused is counted instead of dropped silently.
/// </para>
/// <para>
/// The thread is a background thread with an explicit name, so a stack dump or a profiler attributes
/// durable query time to this component rather than to an anonymous pool thread, and stopping the executor
/// does not keep the process alive.
/// </para>
/// </summary>
public sealed class BoundedActivityQueryExecutor : IActivityQueryExecutor, IDisposable
{
    /// <summary>
    /// Deepest pending queue this executor accepts. The view model never queues more than one item, so a
    /// bound of four is headroom for a burst of operators' actions rather than a tuned limit.
    /// </summary>
    public const int DefaultQueueCapacity = 4;

    public const string DefaultThreadName = "LLMWorkGUI.ActivityQuery";

    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private readonly Thread _worker;
    private readonly int _queueCapacity;
    private bool _isDisposed;
    private Task? _completion;
    private int _pendingHighWaterMark;
    private long _enqueued;
    private long _executed;
    private long _refused;

    public BoundedActivityQueryExecutor(
        int queueCapacity = DefaultQueueCapacity,
        string? threadName = null)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueCapacity),
                queueCapacity,
                "The queue capacity must be positive.");
        }

        _queueCapacity = queueCapacity;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = string.IsNullOrWhiteSpace(threadName) ? DefaultThreadName : threadName
        };
        _worker.Start();
    }

    /// <summary>Name of the thread this executor owns, so query time is attributable.</summary>
    public string ThreadName => _worker.Name ?? DefaultThreadName;

    /// <summary>Maximum number of items this executor will hold pending.</summary>
    public int QueueCapacity => _queueCapacity;

    /// <summary>Deepest pending queue observed. Evidence that the bound held under a burst.</summary>
    public int PendingHighWaterMark
    {
        get
        {
            lock (_gate)
            {
                return _pendingHighWaterMark;
            }
        }
    }

    /// <summary>Work items accepted.</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);

    /// <summary>Work items run to completion.</summary>
    public long ExecutedCount => Interlocked.Read(ref _executed);

    /// <summary>
    /// Work items refused because the bound was reached. Non-zero is a real signal, not noise: it means a
    /// caller queued work faster than the worker drained it, and the screen would show a stale result
    /// until the next refresh.
    /// </summary>
    public long RefusedCount => Interlocked.Read(ref _refused);

    public void Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_isDisposed)
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            if (_queue.Count >= _queueCapacity)
            {
                // Counted, never silently dropped. The work item is a query the screen has already shown
                // as loading, so dropping it is only acceptable if the count above makes it visible.
                _refused++;
                return;
            }

            _queue.Enqueue(work);
            Interlocked.Increment(ref _enqueued);

            if (_queue.Count > _pendingHighWaterMark)
            {
                _pendingHighWaterMark = _queue.Count;
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stops accepting work and waits for the current query before releasing its dependencies.</summary>
    public void Dispose()
    {
        var completion = BeginShutdown();
        if (Thread.CurrentThread != _worker && !completion.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Activity query worker did not finish its accepted queries during shutdown.");
    }

    /// <summary>Retires admission and retains a join task for the actual worker, including accepted pending work.</summary>
    public Task BeginShutdown()
    {
        lock (_gate)
        {
            _isDisposed = true;
            Monitor.PulseAll(_gate);
            // Cancellation of a host wait never cancels this join or claims the physical worker ended.
            return _completion ??= Task.Run(() => _worker.Join());
        }
    }

    private void Loop()
    {
        while (true)
        {
            Action work;

            lock (_gate)
            {
                while (_queue.Count == 0 && !_isDisposed)
                {
                    Monitor.Wait(_gate);
                }

                if (_queue.Count == 0)
                {
                    return;
                }

                work = _queue.Dequeue();
            }

            // The worker is the query's thread and the query's connection belongs to whoever opened it
            // inside the work item, so nothing is held across the call that could outlive it.
            try
            {
                work();
            }
            catch
            {
                // A work item that throws must not take the worker down with it: the next query would then
                // never run and the screen would sit on its loading state for the rest of the session.
                // The item itself is responsible for reporting the failure.
            }
            finally
            {
                Interlocked.Increment(ref _executed);
            }
        }
    }
}

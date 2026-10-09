namespace LLMWorkGUI.Infrastructure.Concurrency;

internal sealed class NamedMutexScope : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _owner;
    private readonly object _disposeGate = new();
    private bool _disposed;

    private NamedMutexScope(string name)
    {
        // A scope can cross async continuations. Only this dedicated thread touches
        // ownership, so neither recursive caller acquisition nor cross-thread release is possible.
        _owner = new Thread(() => OwnMutex(name)) { IsBackground = true, Name = "LLMWorkGUI mutex owner" };
    }

    public static NamedMutexScope? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var scope = new NamedMutexScope(name);
        try
        {
            scope._owner.Start();
            if (scope._acquired.Task.GetAwaiter().GetResult())
                return scope;
        }
        catch
        {
            if ((scope._owner.ThreadState & ThreadState.Unstarted) == 0)
                scope._owner.Join();
            scope._release.Dispose();
            throw;
        }

        scope.Dispose();
        return null;
    }

    private void OwnMutex(string name)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            _acquired.SetResult(acquired);
            if (acquired)
            {
                try { _release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            _finished.SetResult();
        }
        catch (Exception exception)
        {
            // Propagate acquisition/release errors instead of claiming ownership or
            // successful release. No unhandled exception escapes the background thread.
            if (!_acquired.TrySetException(exception))
                _finished.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _release.Set();
            _owner.Join();
            try
            {
                _finished.Task.GetAwaiter().GetResult();
            }
            finally
            {
                _disposed = true;
                _release.Dispose();
            }
        }
    }
}

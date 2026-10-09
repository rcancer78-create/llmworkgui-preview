namespace LLMWorkGUI.Application.Workflows;

public sealed class ScratchWorkspace : IAsyncDisposable, IDisposable
{
    private const int CleanupAttempts = 5;
    private const int CleanupRetryDelayMilliseconds = 100;

    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private int _cleanupCompleted;
    private readonly Action? _beforeCleanup;
    private readonly Action? _afterCleanup;

    public ScratchWorkspace(string directoryPath, ScratchScope scope, string scopeId,
        Action? beforeCleanup = null, Action? afterCleanup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        DirectoryPath = Path.GetFullPath(directoryPath);
        Scope = scope;
        ScopeId = scopeId;
        _beforeCleanup = beforeCleanup;
        _afterCleanup = afterCleanup;
    }

    public string DirectoryPath { get; }

    public ScratchScope Scope { get; }

    public string ScopeId { get; }

    public bool IsCleanedUp => Volatile.Read(ref _cleanupCompleted) == 1;

    /// <summary>Cleanup must not replace a primary failure, particularly cancellation.</summary>
    public async Task CleanupAfterFailureAsync(Exception primaryFailure)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        try { await CleanupWorkspaceAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception)
        {
            primaryFailure.Data["ScratchCleanupPending"] = true;
        }
    }

    public async Task CleanupWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCleanedUp)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Directory.Delete has no asynchronous API. Keep filesystem work and retry
                // callbacks off the caller's UI thread; serialize cleanup ownership above.
                await Task.Run(async () =>
                {
                    _beforeCleanup?.Invoke();
                    await DeleteDirectoryWithRetriesAsync(DirectoryPath, cancellationToken).ConfigureAwait(false);
                    _afterCleanup?.Invoke();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error.Data["ScratchCleanupPending"] = true;
                throw;
            }
            Volatile.Write(ref _cleanupCompleted, 1);
        }

        finally { _cleanupGate.Release(); }
    }

    public void Dispose()
    {
        CleanupWorkspaceAsync().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(CleanupWorkspaceAsync());
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string directoryPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CleanupAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directoryPath, recursive: true);

                return;
            }
            catch (DirectoryNotFoundException)
            {
                // Delete may have lost a child during recursion: confirm the root itself is absent.
                // Unlike Exists, GetAttributes does not turn access/query failures into absence.
                try { _ = File.GetAttributes(directoryPath); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                throw new IOException("Scratch root remains after directory deletion failed.");
            }
            catch (IOException) when (attempt < CleanupAttempts - 1)
            {
                await Task.Delay(CleanupRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < CleanupAttempts - 1)
            {
                await Task.Delay(CleanupRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

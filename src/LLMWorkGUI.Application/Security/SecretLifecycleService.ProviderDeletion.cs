using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Application.Security;

public sealed partial class SecretLifecycleService
{
    public async Task<ProviderDeletionResult> DeleteProviderConfigurationAsync(string providerId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var deletion = _providerDeletion ?? throw new InvalidOperationException("Coordinated provider deletion is unavailable.");
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deleted = await deletion.DeleteConfigurationAsync(providerId, expectedRevision, cancellationToken).ConfigureAwait(false);
            // The transaction already committed. Caller cancellation must not turn successful deletion
            // into a failure acknowledgement; interrupted cleanup remains durably queued for startup.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { return new ProviderDeletionResult(deleted, await DrainSecretCleanupCoreAsync(deadline.Token).ConfigureAwait(false)); }
            catch (Exception exception)
            {
                _logger?.LogWarning("Provider deletion committed; secret cleanup remains pending ({FailureType}).", exception.GetType().Name);
                return new ProviderDeletionResult(deleted, -1);
            }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> RetryPendingSecretCleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_providerDeletion is null) return 0;
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try { return await DrainSecretCleanupCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private async Task<int> DrainSecretCleanupCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pending;
        try { pending = await _providerDeletion!.ListPendingSecretDeletionsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning("Committed secret cleanup queue is unavailable ({FailureType}).", exception.GetType().Name);
            return -1; // The profile operation committed, but the remaining queue cannot be counted.
        }
        var remaining = 0;
        foreach (var reference in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _secretStore.DeleteSecretAsync(reference, cancellationToken).ConfigureAwait(false);
                await _providerDeletion!.CompleteSecretDeletionAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                remaining++;
                _logger?.LogWarning("Committed secret payload cleanup remains pending ({FailureType}).", exception.GetType().Name);
            }
        }
        return remaining;
    }
}

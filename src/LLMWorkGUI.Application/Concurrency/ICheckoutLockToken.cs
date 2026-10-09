namespace LLMWorkGUI.Application.Concurrency;

public interface ICheckoutLockToken : IDisposable, IAsyncDisposable
{
    string LockId { get; }

    string ProjectId { get; }

    string CanonicalRootPath { get; }

    string ExecutionId { get; }

    string ApplicationInstanceId { get; }

    bool IsHeld { get; }

    Task ReleaseAsync(string reason, CancellationToken cancellationToken = default);
}

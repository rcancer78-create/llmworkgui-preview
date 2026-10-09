using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class FakeCheckoutLockToken : ICheckoutLockToken
{
    public string LockId { get; } = "lock-1";

    public string ProjectId { get; } = "project-1";

    public string CanonicalRootPath { get; } = "C:\\workspace\\demo-app";

    public string ExecutionId { get; } = "execution-1";

    public string ApplicationInstanceId { get; } = "instance-1";

    public bool IsHeld { get; private set; } = true;

    public int ReleaseCount { get; private set; }

    public string? LastReleaseReason { get; private set; }

    public Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
    {
        ReleaseCount++;
        LastReleaseReason = reason;
        IsHeld = false;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

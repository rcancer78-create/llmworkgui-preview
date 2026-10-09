using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

/// <summary>
/// Deterministic checkout lock service double. It records every acquisition so UI tests can prove
/// that a lock-requiring turn really takes the lock and that a refused turn never takes it.
/// </summary>
internal sealed class FakeCheckoutLockService : ICheckoutLockService
{
    public List<FakeUiCheckoutLockToken> AcquiredTokens { get; } = new();

    public List<long> AcquiredGenerations { get; } = new();

    /// <summary>When set, acquisition fails with this exception.</summary>
    public Exception? AcquireFailure { get; set; }

    public bool RequiresWriterLock(string? executionMode) => true;

    public bool RequiresWriterLock(WorkflowRole role, string? executionMode) => true;

    public Task<ICheckoutLockToken> AcquireWriterLockAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        CancellationToken cancellationToken = default)
    {
        if (AcquireFailure is not null)
        {
            throw AcquireFailure;
        }

        var token = new FakeUiCheckoutLockToken(projectId, canonicalRootPath, executionId);
        AcquiredTokens.Add(token);
        AcquiredGenerations.Add(processGeneration);

        return Task.FromResult<ICheckoutLockToken>(token);
    }

    public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
        string projectId,
        string canonicalRootPath,
        string executionId,
        long processGeneration,
        string? executionMode,
        WorkflowRole role = WorkflowRole.Unknown,
        CancellationToken cancellationToken = default) =>
        AcquireWriterLockAsync(projectId, canonicalRootPath, executionId, processGeneration, cancellationToken)!;
}

/// <summary>Writer lock token double that records its release reason.</summary>
internal sealed class FakeUiCheckoutLockToken : ICheckoutLockToken
{
    public FakeUiCheckoutLockToken(string projectId, string canonicalRootPath, string executionId)
    {
        ProjectId = projectId;
        CanonicalRootPath = canonicalRootPath;
        ExecutionId = executionId;
    }

    public string LockId { get; } = Guid.NewGuid().ToString("D");

    public string ProjectId { get; }

    public string CanonicalRootPath { get; }

    public string ExecutionId { get; }

    public string ApplicationInstanceId => "ui-test-instance";

    public bool IsHeld { get; private set; } = true;

    public string? ReleaseReason { get; private set; }

    public Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
    {
        ReleaseReason = reason;
        IsHeld = false;

        return Task.CompletedTask;
    }

    public void Dispose() => IsHeld = false;

    public ValueTask DisposeAsync()
    {
        IsHeld = false;

        return ValueTask.CompletedTask;
    }
}

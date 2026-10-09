using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class ProjectLock
{
    private ProjectLock(
        string id,
        string projectId,
        string canonicalRootPath,
        string executionId,
        string applicationInstanceId,
        long processGeneration,
        DateTimeOffset acquiredAt)
    {
        Id = id;
        ProjectId = projectId;
        CanonicalRootPath = canonicalRootPath;
        ExecutionId = executionId;
        ApplicationInstanceId = applicationInstanceId;
        ProcessGeneration = processGeneration;
        AcquiredAt = acquiredAt;
    }

    public string Id { get; }

    public string ProjectId { get; }

    public string CanonicalRootPath { get; }

    public string ExecutionId { get; }

    public string ApplicationInstanceId { get; }

    public long ProcessGeneration { get; }

    public DateTimeOffset AcquiredAt { get; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public string? ReleaseReason { get; private set; }

    public bool IsHeld => ReleasedAt is null;

    public static string CanonicalizeRoot(string rootPath)
    {
        DomainGuard.NotBlank(rootPath, nameof(rootPath));

        if (!Path.IsPathRooted(rootPath))
        {
            throw new ArgumentException("Checkout root must be an absolute path.", nameof(rootPath));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public static ProjectLock Acquire(
        string id,
        string projectId,
        string rootPath,
        string executionId,
        string applicationInstanceId,
        long processGeneration,
        DateTimeOffset acquiredAt)
    {
        if (processGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processGeneration), "Process generation must not be negative.");
        }

        return new ProjectLock(
            DomainGuard.NotBlank(id, nameof(id)),
            DomainGuard.NotBlank(projectId, nameof(projectId)),
            CanonicalizeRoot(rootPath),
            DomainGuard.NotBlank(executionId, nameof(executionId)),
            DomainGuard.NotBlank(applicationInstanceId, nameof(applicationInstanceId)),
            processGeneration,
            acquiredAt);
    }

    public static void EnsureCanAcquire(
        ProjectLock? heldLock,
        string rootPath,
        string executionId,
        bool isReadOnly)
    {
        DomainGuard.NotBlank(executionId, nameof(executionId));

        if (isReadOnly || heldLock is null || !heldLock.IsHeld)
        {
            return;
        }

        var canonicalRootPath = CanonicalizeRoot(rootPath);

        if (!string.Equals(heldLock.CanonicalRootPath, canonicalRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(heldLock.ExecutionId, executionId, StringComparison.Ordinal))
        {
            return;
        }

        throw new ProjectLockConflictException(
            $"Checkout '{canonicalRootPath}' already has an active writer lock owned by execution '{heldLock.ExecutionId}'.");
    }

    public void Release(DateTimeOffset releasedAt, string reason, ExecutionState executionState, SessionState sessionState)
    {
        if (!IsHeld)
        {
            throw new InvalidOperationException("Project lock has already been released.");
        }

        DomainGuard.NotBlank(reason, nameof(reason));

        if (executionState == ExecutionState.Ambiguous || sessionState == SessionState.Orphaned)
        {
            throw new ProjectLockConflictException(
                "A stale project lock must not be released while the linked execution is Orphaned or Ambiguous; reconciliation is required.");
        }

        ReleasedAt = releasedAt;
        ReleaseReason = reason;
    }
}

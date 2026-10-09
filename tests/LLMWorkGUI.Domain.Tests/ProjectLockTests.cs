using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class ProjectLockTests
{
    private const string Root = @"C:\work\demo-app";
    private static readonly DateTimeOffset AcquiredAt = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReleasedAt = new(2026, 9, 22, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Acquire_StoresCanonicalScopeAndOwner()
    {
        var projectLock = AcquireLock();

        Assert.Equal("lock-1", projectLock.Id);
        Assert.Equal("project-1", projectLock.ProjectId);
        Assert.Equal(Root, projectLock.CanonicalRootPath);
        Assert.Equal("execution-1", projectLock.ExecutionId);
        Assert.Equal("app-instance-1", projectLock.ApplicationInstanceId);
        Assert.Equal(1, projectLock.ProcessGeneration);
        Assert.Equal(AcquiredAt, projectLock.AcquiredAt);
        Assert.True(projectLock.IsHeld);
        Assert.Null(projectLock.ReleasedAt);
        Assert.Null(projectLock.ReleaseReason);
    }

    [Fact]
    public void Acquire_CanonicalizesRootPath()
    {
        var projectLock = ProjectLock.Acquire(
            "lock-1",
            "project-1",
            @"C:\work\demo-app\sub\..\",
            "execution-1",
            "app-instance-1",
            1,
            AcquiredAt);

        Assert.Equal(Root, projectLock.CanonicalRootPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Acquire_RejectsBlankId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire(value!, "project-1", Root, "execution-1", "app-instance-1", 1, AcquiredAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Acquire_RejectsBlankProjectId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire("lock-1", value!, Root, "execution-1", "app-instance-1", 1, AcquiredAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Acquire_RejectsBlankRootPath(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire("lock-1", "project-1", value!, "execution-1", "app-instance-1", 1, AcquiredAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Acquire_RejectsBlankExecutionId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire("lock-1", "project-1", Root, value!, "app-instance-1", 1, AcquiredAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Acquire_RejectsBlankApplicationInstanceId(string? value)
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire("lock-1", "project-1", Root, "execution-1", value!, 1, AcquiredAt));
    }

    [Fact]
    public void Acquire_RejectsRelativeRootPath()
    {
        Assert.Throws<ArgumentException>(
            () => ProjectLock.Acquire("lock-1", "project-1", @"demo-app", "execution-1", "app-instance-1", 1, AcquiredAt));
    }

    [Fact]
    public void Acquire_RejectsNegativeProcessGeneration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProjectLock.Acquire("lock-1", "project-1", Root, "execution-1", "app-instance-1", -1, AcquiredAt));
    }

    [Theory]
    [InlineData(@"C:\work\demo-app\.")]
    [InlineData(@"C:\work\demo-app\sub\..")]
    [InlineData(@"C:\work\demo-app\")]
    public void CanonicalizeRoot_NormalizesEquivalentRoots(string rootPath)
    {
        Assert.Equal(Root, ProjectLock.CanonicalizeRoot(rootPath));
    }

    [Fact]
    public void EnsureCanAcquire_WithoutHeldLock_IsAllowed()
    {
        ProjectLock.EnsureCanAcquire(null, Root, "execution-2", false);
    }

    [Fact]
    public void EnsureCanAcquire_SameWriterExecution_IsAllowed()
    {
        var heldLock = AcquireLock();

        ProjectLock.EnsureCanAcquire(heldLock, Root, "execution-1", false);
    }

    [Fact]
    public void EnsureCanAcquire_SecondWriterOnSameCheckout_IsRejected()
    {
        var heldLock = AcquireLock();

        var exception = Assert.Throws<ProjectLockConflictException>(
            () => ProjectLock.EnsureCanAcquire(heldLock, Root, "execution-2", false));

        Assert.Contains("execution-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCanAcquire_SecondWriterOnSameCheckout_IsCaseInsensitive()
    {
        var heldLock = AcquireLock();

        Assert.Throws<ProjectLockConflictException>(
            () => ProjectLock.EnsureCanAcquire(heldLock, @"c:\WORK\demo-app", "execution-2", false));
    }

    [Fact]
    public void EnsureCanAcquire_WriterOnDifferentCheckout_IsAllowed()
    {
        var heldLock = AcquireLock();

        ProjectLock.EnsureCanAcquire(heldLock, @"C:\work\other-app", "execution-2", false);
    }

    [Fact]
    public void EnsureCanAcquire_ReadOnlyExecution_IsAllowedWhileWriterLockHeld()
    {
        var heldLock = AcquireLock();

        ProjectLock.EnsureCanAcquire(heldLock, Root, "execution-2", true);
    }

    [Fact]
    public void EnsureCanAcquire_ReleasedLock_IsAllowed()
    {
        var heldLock = AcquireLock();
        heldLock.Release(ReleasedAt, "execution completed", ExecutionState.Succeeded, SessionState.Idle);

        ProjectLock.EnsureCanAcquire(heldLock, Root, "execution-2", false);
    }

    [Fact]
    public void EnsureCanAcquire_RejectsBlankExecutionId()
    {
        Assert.Throws<ArgumentException>(() => ProjectLock.EnsureCanAcquire(null, Root, " ", false));
    }

    [Fact]
    public void Release_MarksLockReleasedAndRecordsReason()
    {
        var projectLock = AcquireLock();

        projectLock.Release(ReleasedAt, "terminal outcome recorded", ExecutionState.Succeeded, SessionState.Idle);

        Assert.False(projectLock.IsHeld);
        Assert.Equal(ReleasedAt, projectLock.ReleasedAt);
        Assert.Equal("terminal outcome recorded", projectLock.ReleaseReason);
    }

    [Theory]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.TimedOut)]
    [InlineData(ExecutionState.Cancelled)]
    [InlineData(ExecutionState.RouteMismatch)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void Release_IsAllowedForNonAmbiguousExecutions(ExecutionState executionState)
    {
        var projectLock = AcquireLock();

        projectLock.Release(ReleasedAt, "release", executionState, SessionState.Idle);

        Assert.False(projectLock.IsHeld);
    }

    [Fact]
    public void Release_IsRejectedForAmbiguousExecution()
    {
        var projectLock = AcquireLock();

        var exception = Assert.Throws<ProjectLockConflictException>(
            () => projectLock.Release(ReleasedAt, "release", ExecutionState.Ambiguous, SessionState.Idle));

        Assert.True(projectLock.IsHeld);
        Assert.Contains("reconciliation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_IsRejectedForOrphanedSession()
    {
        var projectLock = AcquireLock();

        Assert.Throws<ProjectLockConflictException>(
            () => projectLock.Release(ReleasedAt, "release", ExecutionState.Failed, SessionState.Orphaned));

        Assert.True(projectLock.IsHeld);
    }

    [Fact]
    public void Release_Twice_IsRejected()
    {
        var projectLock = AcquireLock();
        projectLock.Release(ReleasedAt, "release", ExecutionState.Succeeded, SessionState.Idle);

        Assert.Throws<InvalidOperationException>(
            () => projectLock.Release(ReleasedAt, "release", ExecutionState.Succeeded, SessionState.Idle));
    }

    [Fact]
    public void Release_RequiresReason()
    {
        var projectLock = AcquireLock();

        Assert.Throws<ArgumentException>(
            () => projectLock.Release(ReleasedAt, " ", ExecutionState.Succeeded, SessionState.Idle));

        Assert.True(projectLock.IsHeld);
    }

    [Fact]
    public void ReleasedLock_CanBeReplacedByNewWriter()
    {
        var firstLock = AcquireLock();
        firstLock.Release(ReleasedAt, "release", ExecutionState.Succeeded, SessionState.Idle);

        ProjectLock.EnsureCanAcquire(firstLock, Root, "execution-2", false);

        var secondLock = ProjectLock.Acquire(
            "lock-2",
            "project-1",
            Root,
            "execution-2",
            "app-instance-1",
            2,
            ReleasedAt);

        Assert.True(secondLock.IsHeld);
        Assert.Equal("execution-2", secondLock.ExecutionId);
    }

    private static ProjectLock AcquireLock() =>
        ProjectLock.Acquire("lock-1", "project-1", Root, "execution-1", "app-instance-1", 1, AcquiredAt);
}

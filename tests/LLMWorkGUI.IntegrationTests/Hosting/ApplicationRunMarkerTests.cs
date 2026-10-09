using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Hosting;

public sealed class ApplicationRunMarkerTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private SqliteConnectionFactory Factory => new(_directory.GetPath("probe.db"));
    private string MarkerPath => Factory.DatabasePath + ".running";
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void CleanShutdownMakesNextStartupClean()
    {
        var first = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.False(first.Begin());
        Assert.True(File.Exists(MarkerPath));
        first.CompleteGracefulShutdown();
        Assert.False(File.Exists(MarkerPath));
        var next = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.False(next.Begin());
        next.CompleteGracefulShutdown();
    }

    [Fact]
    public void AbandonedLifetimeIsDetectedWithoutClearingItOnDisposal()
    {
        var first = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.False(first.Begin());
        // No successful shutdown: exactly the on-disk state a killed GUI leaves behind.
        var restarted = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.True(restarted.Begin());
        restarted.CompleteGracefulShutdown();
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void SecondaryLifetimeCannotCreateReplaceOrDeletePrimaryMarker()
    {
        var secondary = new ApplicationRunMarker(Factory, new Guard(false));
        Assert.False(secondary.Begin());
        Assert.False(File.Exists(MarkerPath));
        var primary = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.False(primary.Begin());
        var contents = File.ReadAllText(MarkerPath);
        Assert.False(secondary.Begin());
        secondary.CompleteGracefulShutdown();
        Assert.Equal(contents, File.ReadAllText(MarkerPath));
        primary.CompleteGracefulShutdown();
    }

    [Fact]
    public void StaleOwnerCannotDeleteLaterLifetimeMarker()
    {
        var stale = new ApplicationRunMarker(Factory, new Guard(true));
        stale.Begin();
        var current = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.True(current.Begin());
        var contents = File.ReadAllText(MarkerPath);
        stale.CompleteGracefulShutdown();
        Assert.Equal(contents, File.ReadAllText(MarkerPath));
        current.CompleteGracefulShutdown();
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void DuplicateBeginDoesNotReplaceLifetimeEvidence()
    {
        var marker = new ApplicationRunMarker(Factory, new Guard(true));
        marker.Begin();
        var contents = File.ReadAllText(MarkerPath);
        Assert.Throws<InvalidOperationException>(() => marker.Begin());
        Assert.Equal(contents, File.ReadAllText(MarkerPath));
        marker.CompleteGracefulShutdown();
    }

    [Fact]
    public void DisposedRealSupervisorCannotReplaceTheNewOwnersMarker()
    {
        var staleGuard = new ApplicationInstanceGuard(_directory.Root);
        Assert.True(staleGuard.IsPrimarySupervisor);
        var stale = new ApplicationRunMarker(Factory, staleGuard);
        staleGuard.Dispose();
        using var currentGuard = new ApplicationInstanceGuard(_directory.Root);
        Assert.True(currentGuard.IsPrimarySupervisor);
        var current = new ApplicationRunMarker(Factory, currentGuard);
        current.Begin();
        var currentToken = File.ReadAllText(MarkerPath);

        Assert.Throws<ObjectDisposedException>(() => stale.Begin());
        Assert.Equal(currentToken, File.ReadAllText(MarkerPath));
        current.CompleteGracefulShutdown();
    }

    [Fact]
    public async Task RestartWaitsForTemporaryReadHandleWithoutLosingInterruptionEvidence()
    {
        var first = new ApplicationRunMarker(Factory, new Guard(true));
        Assert.False(first.Begin());
        Task<bool> restart;
        var next = new ApplicationRunMarker(Factory, new Guard(true));
        using (var reader = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            restart = Task.Run(next.Begin);
            Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(_directory.Root, "probe.db.running.*.tmp").Length > 0,
                TimeSpan.FromSeconds(2)));
            Assert.False(restart.IsCompleted);
        }
        Assert.True(await restart.WaitAsync(TimeSpan.FromSeconds(3)));
        next.CompleteGracefulShutdown();
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void PersistentAccessFailurePreservesOldMarkerAndDoesNotBeginLifetime()
    {
        var first = new ApplicationRunMarker(Factory, new Guard(true));
        first.Begin();
        var oldContents = File.ReadAllText(MarkerPath);
        var next = new ApplicationRunMarker(Factory, new Guard(true));
        using (var reader = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = Record.Exception(() => next.Begin());
            Assert.True(exception is IOException or UnauthorizedAccessException);
            next.CompleteGracefulShutdown();
            Assert.Equal(oldContents, File.ReadAllText(MarkerPath));
        }
        Assert.Empty(Directory.GetFiles(_directory.Root, "probe.db.running.*.tmp"));
        Assert.True(next.Begin());
        next.CompleteGracefulShutdown();
    }

    private sealed class Guard(bool primary) : IApplicationInstanceGuard
    {
        public string InstanceId => "synthetic-marker-test";
        public bool IsPrimarySupervisor => primary;
        public bool IsViewOnly => !primary;
        public void EnsureSupervisorPermitted() { if (!primary) throw new SecondaryInstanceReadOnlyException("View only"); }
        public void Dispose() { }
    }
}

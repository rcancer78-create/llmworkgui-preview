using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using DomainReconciliationOutcome = LLMWorkGUI.Domain.Enums.ReconciliationOutcome;

namespace LLMWorkGUI.IntegrationTests.Hosting;

public sealed class StartupReconciliationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_OnPrimaryInstance_QuarantinesAbandonedSessionsWithUnconfirmedDelivery()
    {
        await SeedAbandonedSessionsAsync();

        // This case exercises a vanished process with an available backend. The
        // separate BackendMissing cases cover machines without an installed CLI.
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _database.Root)
            .ConfigureServices(services => services.AddSingleton<ICliExecutableLocator>(
                new AvailableBackendLocator(Path.Combine(_database.Root, "opencode-fixture.exe"))))
            .Build();

        await HostBootstrapper.InitializeAsync(host);

        var sessions = host.Services.GetRequiredService<ISessionRepository>();

        var starting = await sessions.GetByIdAsync("session-starting");
        var active = await sessions.GetByIdAsync("session-active");

        Assert.NotNull(starting);
        Assert.NotNull(active);
        // A vanished process does not establish whether its prompt reached the backend.
        Assert.Equal(SessionState.Ambiguous, starting!.State);
        Assert.Equal(SessionState.Ambiguous, active!.State);
        Assert.Equal(DomainReconciliationOutcome.Ambiguous, starting.ReconciliationOutcome);
        Assert.Equal(DomainReconciliationOutcome.Ambiguous, active.ReconciliationOutcome);
        Assert.Equal("execution-starting", starting.ActiveExecutionId);
        Assert.Equal("execution-active", active.ActiveExecutionId);
        var executions = host.Services.GetRequiredService<IExecutionRepository>();
        foreach (var id in new[] { "execution-starting", "execution-active" })
        {
            var execution = Assert.IsType<LLMWorkGUI.Domain.Entities.Execution>(await executions.GetByIdAsync(id));
            Assert.Equal(ExecutionState.Ambiguous, execution.State);
            Assert.Null(execution.EndedAt);
        }
    }

    [Fact]
    public async Task InitializeAsync_OnViewOnlyInstance_SkipsSessionReconciliation()
    {
        await SeedAbandonedSessionsAsync();

        var guard = new StubInstanceGuard(isPrimarySupervisor: false);
        var reconciliation = new SpyReconciliationService();

        using var host = HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _database.Root)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IApplicationInstanceGuard>(guard);
                services.AddSingleton<IReconciliationService>(reconciliation);
            })
            .Build();

        await HostBootstrapper.InitializeAsync(host);

        Assert.Equal(0, reconciliation.ReconcileAllActiveCallCount);

        var sessions = host.Services.GetRequiredService<ISessionRepository>();

        var starting = await sessions.GetByIdAsync("session-starting");
        var active = await sessions.GetByIdAsync("session-active");

        Assert.NotNull(starting);
        Assert.NotNull(active);
        Assert.Equal(SessionState.Starting, starting!.State);
        Assert.Equal(SessionState.Active, active!.State);
        Assert.Equal(DomainReconciliationOutcome.None, starting.ReconciliationOutcome);
        Assert.Equal(DomainReconciliationOutcome.None, active.ReconciliationOutcome);
    }

    private async Task SeedAbandonedSessionsAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        await _database.SeedSessionAsync(sessionId: "session-starting", state: "Starting");
        await _database.SeedExecutionAsync(
            "execution-starting",
            sessionId: "session-starting",
            state: "Starting",
            processState: $"pid:{int.MaxValue};name:opencode");

        await _database.SeedSessionAsync(
            sessionId: "session-active",
            state: "Active",
            nativeSessionId: "native-active");
        await _database.SeedExecutionAsync(
            "execution-active",
            sessionId: "session-active",
            state: "Starting",
            processState: $"pid:{int.MaxValue};name:opencode");
    }

    private sealed class AvailableBackendLocator(string path) : ICliExecutableLocator
    {
        public Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The reconciliation probe checks availability, then the persisted PID;
            // it never executes this synthetic path or searches the user's PATH.
            return Task.FromResult<string?>(path);
        }
    }

    private sealed class StubInstanceGuard : IApplicationInstanceGuard
    {
        public StubInstanceGuard(bool isPrimarySupervisor)
        {
            IsPrimarySupervisor = isPrimarySupervisor;
        }

        public string InstanceId => "startup-reconciliation-test-instance";

        public bool IsPrimarySupervisor { get; }

        public bool IsViewOnly => !IsPrimarySupervisor;

        public void EnsureSupervisorPermitted()
        {
            if (IsViewOnly)
            {
                throw new SecondaryInstanceReadOnlyException(
                    "The stub instance runs in View-Only mode.");
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class SpyReconciliationService : IReconciliationService
    {
        public int ReconcileAllActiveCallCount { get; private set; }

        public Task<ReconciliationEvidence> ReconcileSessionAsync(
            string localSessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ReconciliationEvidence>> ReconcileAllActiveAsync(
            CancellationToken cancellationToken = default)
        {
            ReconcileAllActiveCallCount++;

            return Task.FromResult<IReadOnlyList<ReconciliationEvidence>>(
                Array.Empty<ReconciliationEvidence>());
        }

        public Task<ReconciliationRecoveryResult> ApplyRecoveryActionAsync(
            string localSessionId,
            RecoveryAction action,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

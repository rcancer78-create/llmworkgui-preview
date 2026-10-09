using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed class ReconciliationViewOnlyTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData("single")]
    [InlineData("all")]
    [InlineData("close")]
    public async Task ProductionServiceRejectsViewOnlyCommandsWithoutChangingRuntimeOwnership(string command)
    {
        await SeedAsync(terminal: command == "close");
        var before = await SnapshotAsync();
        using var host = BuildHost(new Guard(primary: false));
        var service = host.Services.GetRequiredService<IReconciliationService>();
        Assert.IsType<ReconciliationService>(service);

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => InvokeAsync(service, command));

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task PrimaryProductionServiceCanCloseConfirmedTerminalSession()
    {
        await SeedAsync(terminal: true);
        using var host = BuildHost(new Guard(primary: true));

        var result = await host.Services.GetRequiredService<IReconciliationService>()
            .ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession);

        Assert.Equal("lock-1", result.ReleasedLockId);
        var session = await host.Services.GetRequiredService<ISessionRepository>().GetByIdAsync("session-1");
        Assert.Equal(SessionState.Closed, session!.State);
        Assert.Null(session.ActiveExecutionId);
        var execution = await host.Services.GetRequiredService<IExecutionRepository>().GetByIdAsync("execution-1");
        Assert.Equal(ExecutionState.Succeeded, execution!.State);
        Assert.NotNull(execution.EndedAt);
        Assert.Null(await host.Services.GetRequiredService<IProjectLockRepository>()
            .GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Single(await host.Services.GetRequiredService<IExecutionRepository>().ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task SqliteRecoveryWithoutInstanceAuthorityCannotMutateOwnership()
    {
        await SeedAsync(terminal: true);
        var before = await SnapshotAsync();
        var service = new ReconciliationService(new SqliteSessionRepository(_database.Factory),
            new SqliteExecutionRepository(_database.Factory, new SensitiveDataFilter()),
            new SqliteProjectRepository(_database.Factory), new SqliteProjectLockRepository(_database.Factory),
            new GoneProbe(), new RecoveryMatrix(), connectionFactory: _database.Factory);

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() =>
            service.ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task AuthorityLostAfterPreflightCannotCommitSessionLockOrAudit()
    {
        await SeedAsync(terminal: true);
        var before = await SnapshotAsync();
        var guard = new Guard(primary: true);
        var locks = new RevokingLockRead(new SqliteProjectLockRepository(_database.Factory), guard);
        using var host = BuildHost(guard, locks);

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() =>
            host.Services.GetRequiredService<IReconciliationService>()
                .ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.True(locks.Revoked);
        Assert.Equal(before, await SnapshotAsync());
    }

    private IHost BuildHost(Guard guard, IProjectLockRepository? locks = null) => HostBootstrapper
        .CreateHostBuilder(appDataDirectory: _database.Root)
        .ConfigureServices(services =>
        {
            services.AddSingleton<IApplicationInstanceGuard>(guard);
            services.AddSingleton<IReconciliationProbe>(new GoneProbe());
            if (locks is not null) services.AddSingleton<IProjectLockRepository>(locks);
        }).Build();

    private static async Task InvokeAsync(IReconciliationService service, string command)
    {
        switch (command)
        {
            case "single": await service.ReconcileSessionAsync("session-1"); break;
            case "all": await service.ReconcileAllActiveAsync(); break;
            case "close": await service.ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession); break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private async Task SeedAsync(bool terminal)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync(state: terminal ? "Ambiguous" : "Active", nativeSessionId: "native-1",
            activeExecutionId: "execution-1");
        await _database.SeedExecutionAsync("execution-1", state: terminal ? "Succeeded" : "Running",
            processState: "NativePromptDeliveryUnconfirmed",
            endedAt: terminal ? new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero) : null);
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp';
            UPDATE Routes SET Backend='CursorAcp'; UPDATE Sessions SET Backend='CursorAcp';
            UPDATE Executions SET StartedAtUtc=CreatedAtUtc;
            """;
        await update.ExecuteNonQueryAsync();
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");
    }

    private async Task<string> SnapshotAsync()
    {
        var rows = new List<string>();
        await using var connection = await _database.Factory.OpenConnectionAsync();
        foreach (var table in new[] { "Sessions", "Executions", "ProjectLocks", "ExecutionEvents" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY Id";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(table + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index).ToString()).ToArray()));
        }
        return string.Join('\n', rows);
    }

    private sealed class Guard(bool primary) : IApplicationInstanceGuard
    {
        public string InstanceId => "reconciliation-authority-fixture";
        public bool IsPrimarySupervisor { get; private set; } = primary;
        public bool IsViewOnly => !IsPrimarySupervisor;
        public void Revoke() => IsPrimarySupervisor = false;
        public void EnsureSupervisorPermitted()
        {
            if (IsViewOnly) throw new SecondaryInstanceReadOnlyException("View-only fixture has no mutation authority.");
        }
        public void Dispose() { }
    }

    private sealed class GoneProbe : IReconciliationProbe
    {
        public Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken token = default)
            => Task.FromResult(new ReconciliationProbeResult { BackendAvailable = true, ProcessAlive = false,
                Details = "Synthetic missing process fixture; no native process or model call." });
    }

    private sealed class RevokingLockRead(IProjectLockRepository inner, Guard guard) : IProjectLockRepository
    {
        public bool Revoked { get; private set; }
        public async Task<ProjectLock?> GetActiveByRootPathAsync(string root, CancellationToken token = default)
        {
            var held = await inner.GetActiveByRootPathAsync(root, token);
            guard.Revoke();
            Revoked = true;
            return held;
        }
        public Task<bool> TryAcquireAsync(ProjectLock value, CancellationToken token = default) => inner.TryAcquireAsync(value, token);
        public Task<bool> ReleaseAsync(string id, DateTimeOffset at, string reason, CancellationToken token = default) => inner.ReleaseAsync(id, at, reason, token);
    }
}

internal sealed class PrimaryReconciliationTestGuard : IApplicationInstanceGuard
{
    public string InstanceId => "primary-reconciliation-fixture";
    public bool IsPrimarySupervisor => true;
    public bool IsViewOnly => false;
    public void EnsureSupervisorPermitted() { }
    public void Dispose() { }
}

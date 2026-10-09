using System.IO;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Cursor dispatcher isolation")]
public sealed class CursorUiOwnedWriterRetirementReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposedPanelSettlesAnUndispatchedRealWriterOnlyAfterDurableRejection(bool rejectJournalCommit)
    {
        var root = Directory.CreateTempSubdirectory("LLMWorkGUI-owned-cursor-retirement-");
        ISqliteConnectionFactory? factory = null;
        HeldLocks? locks = null;
        Task? operation = null;
        CursorWorkspaceViewModel? model = null;
        try
        {
            using var guard = new ApplicationInstanceGuard(root.FullName);
            var services = new ServiceCollection();
            services.AddSingleton<IApplicationInstanceGuard>(guard);
            services.AddInfrastructure(root.FullName);
            using var provider = services.BuildServiceProvider();
            factory = provider.GetRequiredService<ISqliteConnectionFactory>();
            await new DatabaseMigrator(factory).MigrateAsync();
            await Execute(factory, """
                INSERT INTO ProviderProfiles(Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                    VALUES('owned-profile','Owned','CursorAcp','PrivateSource',1,'2026-10-06T00:00:00Z','2026-10-06T00:00:00Z');
                INSERT INTO Accounts(Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
                    VALUES('owned-account','owned-profile','Owned','Valid','Healthy','2026-10-06T00:00:00Z','2026-10-06T00:00:00Z');
                INSERT INTO Models(Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,Health,DiscoveredAtUtc)
                    VALUES('owned-model','CursorAcp','owned-profile','owned/native-model','Owned','Supported','UserDefined',1,'Healthy','2026-10-06T00:00:00Z');
                INSERT INTO Routes(Id,Backend,ProviderProfileId,AccountId,ModelId,ExecutionMode,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                    VALUES('owned-route','CursorAcp','owned-profile','owned-account','owned-model','agent','PrivateSource',1,'Healthy','2026-10-06T00:00:00Z','2026-10-06T00:00:00Z');
                INSERT INTO Projects(Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc)
                    VALUES('owned-project','Owned',$root,'PrivateSource','2026-10-06T00:00:00Z','2026-10-06T00:00:00Z');
                """, root.FullName);
            var actualJournal = new SqliteCursorAcpExecutionJournal(factory, TimeProvider.System, guard);
            var journal = new CountingJournal(actualJournal);
            locks = new HeldLocks(provider.GetRequiredService<ICheckoutLockService>(), factory);
            var lifecycle = new FakeCursorAcpSessionLifecycleService
            {
                StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
                SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "owned-native-session" })
            };
            model = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy(), locks, new AllowedGate(),
                provider.GetRequiredService<IProjectRepository>(), executionJournal: journal) { ProjectId = "owned-project" };
            var route = Assert.Single(await actualJournal.ListRoutesAsync());
            model.Routes.Add(route); model.SelectedRoute = route;
            await model.StartBackendAsync();
            await model.CreateSessionAsync(root.FullName);
            model.SelectedModeId = "agent";
            model.SelectedModeState = CapabilityState.Supported;
            model.SelectedModeAccess = CursorAcpModeAccess.Write;
            model.PromptInput = "Owned synthetic undispatched prompt";
            Assert.True(model.CanSendPrompt);
            operation = model.SendPromptAsync();
            await locks.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("SessionConfirmed", await Scalar(factory, "SELECT State FROM Executions"));
            Assert.Equal("1", await Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            if (rejectJournalCommit)
                await Execute(factory, "CREATE TRIGGER refuse_owned_rejection BEFORE UPDATE OF State ON Executions WHEN NEW.State='Failed' BEGIN SELECT RAISE(ABORT,'owned fixture commit refusal'); END;");
            model.Dispose(); locks.ReturnToken.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(lifecycle.TurnRequests);
            Assert.Equal(0, lifecycle.StopCount);
            Assert.Equal(1, journal.CompleteCalls);
            if (rejectJournalCommit)
            {
                Assert.True(locks.Token!.IsHeld);
                Assert.Equal(0, locks.ReleaseCalls);
                Assert.Equal("1", await Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
                Assert.Equal("SessionConfirmed", await Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal("1", await Scalar(factory, "SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
            }
            else
            {
                Assert.Equal("Failed", await Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal("Rejected", await Scalar(factory, "SELECT TerminationReason FROM Executions"));
                Assert.Equal("0", await Scalar(factory, "SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
                Assert.Equal(1, locks.ReleaseCalls);
                Assert.False(locks.Token!.IsHeld);
                Assert.Equal("0", await Scalar(factory, "SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            }
        }
        finally
        {
            model?.Dispose(); locks?.ReturnToken.TrySetResult();
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
            // Owned fixture teardown only, after assertions and actual operation completion. A
            // retained Ambiguous row must not make token.Dispose mask the behavioral assertion.
            // No native turn was dispatched by this fixture; retire only its synthetic SQL owner.
            if (factory is not null && locks?.Token is { IsHeld: true } retained)
            {
                await Execute(factory, """
                    DROP TRIGGER IF EXISTS refuse_owned_rejection;
                    UPDATE Executions SET State='Failed' WHERE Id IN (SELECT ExecutionId FROM ProjectLocks WHERE ReleasedAtUtc IS NULL);
                    UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL;
                    """);
                await retained.ReleaseAsync("owned fixture teardown after assertions");
            }
            if (factory is not null)
            {
                using var pool = factory.CreateConnection(); SqliteConnection.ClearPool(pool);
            }
            root.Delete(recursive: true);
        }
    }

    private static async Task<string?> Scalar(ISqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(); return value is null or DBNull ? null : Convert.ToString(value);
    }
    private static async Task Execute(ISqliteConnectionFactory factory, string sql, string? root = null)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        if (root is not null) command.Parameters.AddWithValue("$root", root);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class AllowedGate : IDataClassificationGate
    {
        public Task<DataClassificationGateDecision> EvaluateAsync(DataClassification classification, string? providerId,
            bool isManualOnly = false, CancellationToken token = default) => Task.FromResult(DataClassificationGateDecision.Allowed());
    }
    private sealed class CountingJournal(ICursorAcpExecutionJournal inner) : ICursorAcpExecutionJournal
    {
        public int CompleteCalls;
        public Task<IReadOnlyList<CursorAcpStoredRoute>> ListRoutesAsync(CancellationToken token = default) => inner.ListRoutesAsync(token);
        public Task<CursorAcpJournalEntry> BeginAsync(string project, string root, string native, CursorAcpStoredRoute route,
            string mode, string request, string hash, CancellationToken token = default) => inner.BeginAsync(project, root, native, route, mode, request, hash, token);
        public Task CompleteAsync(CursorAcpJournalEntry entry, CursorAcpTurnResult result, CancellationToken token = default)
        { CompleteCalls++; return inner.CompleteAsync(entry, result, token); }
    }
    private sealed class HeldLocks(ICheckoutLockService inner, ISqliteConnectionFactory factory) : ICheckoutLockService
    {
        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReturnToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ICheckoutLockToken? Token;
        public int ReleaseCalls;
        public bool RequiresWriterLock(string? mode) => inner.RequiresWriterLock(mode);
        public bool RequiresWriterLock(WorkflowRole role, string? mode) => inner.RequiresWriterLock(role, mode);
        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string project, string root, string execution,
            long generation, string? mode, WorkflowRole role = WorkflowRole.Unknown, CancellationToken token = default) =>
            inner.AcquireLockForExecutionAsync(project, root, execution, generation, mode, role, token);
        public async Task<ICheckoutLockToken> AcquireWriterLockAsync(string project, string root, string execution, long generation, CancellationToken token = default)
        {
            Token = await inner.AcquireWriterLockAsync(project, root, execution, generation, token);
            Acquired.TrySetResult(); await ReturnToken.Task;
            return new Lease(Token, this, factory);
        }
        private sealed class Lease(ICheckoutLockToken inner, HeldLocks owner, ISqliteConnectionFactory factory) : ICheckoutLockToken
        {
            public string LockId => inner.LockId;
            public string ProjectId => inner.ProjectId;
            public string CanonicalRootPath => inner.CanonicalRootPath;
            public string ExecutionId => inner.ExecutionId;
            public string ApplicationInstanceId => inner.ApplicationInstanceId;
            public bool IsHeld => inner.IsHeld;
            public async Task ReleaseAsync(string reason, CancellationToken token = default)
            {
                Assert.Equal("Failed", await Scalar(factory, "SELECT State FROM Executions"));
                Assert.Equal("Rejected", await Scalar(factory, "SELECT TerminationReason FROM Executions"));
                owner.ReleaseCalls++; await inner.ReleaseAsync(reason, token);
            }
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

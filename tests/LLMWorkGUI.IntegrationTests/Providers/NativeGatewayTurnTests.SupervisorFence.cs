using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Fact]
    public async Task OrdinaryNativeDispatch_RefusesRealProcessWhenSupervisorDisposedInsideRunningCommit()
    {
        await Ready();
        var paths = CreateDispatchProcess();
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<CodexAdapter>.Instance), paths.Shim);
        var factory = new SupervisorDisposingFactory(_db.Factory, () => _guard!.Dispose());
        _service = new NativeGatewayTurnService(() => _gateway!, factory, _guard!, _locks, _health,
            new SensitiveDataFilter(), TimeProvider.System, _activity);
        await Sql("""
            CREATE TRIGGER DisposeBeforeRunningCommit AFTER INSERT ON ExecutionEvents
            WHEN NEW.EventKind='NativeGatewayLifecycle' AND json_extract(NEW.NormalizedRedactedPayloadJson,'$.state')='Running'
            BEGIN SELECT dispose_native_supervisor(); END;
            """);
        try
        {
            var pending = _service.ExecuteAsync(_request);
            _ = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(pending.IsCompleted);
            Assert.Equal(1, factory.Disposals);
            Assert.False(File.Exists(paths.Sent), "A disposed supervisor must not send the ordinary native prompt.");
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='NativeGatewayLifecycle' AND json_extract(NormalizedRedactedPayloadJson,'$.state')='Running'"));
        }
        finally
        {
            // The synthetic CLI has completed or never started. Fixture-only local reconciliation
            // permits exact held mutex teardown without asserting uncertain production release.
            await Sql("UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL; DROP TRIGGER DisposeBeforeRunningCommit;");
            foreach (var token in _tokens) await token.ReleaseAsync("synthetic native fence fixture cleanup");
        }
    }

    private sealed class SupervisorDisposingFactory(ISqliteConnectionFactory inner, Action dispose) : ISqliteConnectionFactory
    {
        public int Disposals { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection()
        {
            var connection = inner.CreateConnection();
            connection.CreateFunction("dispose_native_supervisor", () => { Disposals++; dispose(); return 1; });
            return connection;
        }
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            try { await connection.OpenAsync(cancellationToken); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
}

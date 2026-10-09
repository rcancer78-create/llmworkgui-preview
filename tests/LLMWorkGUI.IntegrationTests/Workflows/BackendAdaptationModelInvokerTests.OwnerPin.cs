using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    [Fact]
    [Trait("Evidence", "ActualOpenCodeNoModel")]
    public async Task Review_ActualNativeOwnerCannotStopWhileItsCreateAuthorizationIsStillInFlight()
    {
        using var f = await RuntimeFixture();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "opencode-ai", "bin", "opencode.exe");
        Assert.True(File.Exists(executable), "This actual-native regression requires the installed OpenCode binary; a synthetic peer is not equivalent evidence.");
        await using (var connection = await f.Db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ProviderProfiles SET ExecutablePath=$exe; UPDATE Models SET ProviderModelId='openai/gpt-5';";
            command.Parameters.AddWithValue("$exe", executable);
            await command.ExecuteNonQueryAsync();
        }
        var identity = new AdaptationRouteIdentity("account-1", "provider-1", BackendType.OpenCode, "openai/gpt-5");
        var raw = new AdaptationModelRequest(identity.RouteId, identity.BackendModelId!,
            "Owned no-model lifecycle fixture", [new("user", "No provider or model request may be sent")])
            { ProjectId = "project-1", SourceVersionId = "material-version" };
        var request = raw with { AdmissionId = await f.Egress.PrepareAsync(raw, default) };
        var factory = new PausedCreateAuthorizationFactory(f.Db.Factory);
        // Only the transport factory is paused. Actual registry readiness, credentials, physical process,
        // /health and configuration validation use the untouched production registry and base factory.
        var transport = new SqliteAdaptationTransportPolicy(factory,
            f.Services.GetRequiredService<IApplicationInstanceGuard>(),
            f.Services.GetRequiredService<IWorkflowSecretScanner>(), TimeProvider.System, f.Registry);
        var invoker = new BackendAdaptationModelInvoker(f.Services.GetRequiredService<IProviderProfileRepository>(),
            f.Services.GetRequiredService<IAccountRepository>(), f.Egress, f.Registry, transport);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var invocation = invoker.InvokeModelAsync(request, deadline.Token);
        Task? stop = null;
        bool stoppedBeforeAuthorizationRetired = false;
        long activeWhileHeld = -1;
        Exception? invocationFailure = null;
        try
        {
            var reached = await Task.WhenAny(factory.Entered.Task, invocation).WaitAsync(TimeSpan.FromSeconds(60));
            if (ReferenceEquals(reached, invocation))
            {
                try { await invocation; }
                catch (Exception failure)
                {
                    // Type and code stack identify the early readiness/authorization refusal;
                    // Message/Data may contain transport details and are deliberately omitted.
                    throw new Xunit.Sdk.XunitException(
                        "Native invocation failed before the owned create-authorization boundary. "
                        + failure.GetType().FullName + Environment.NewLine + failure.StackTrace);
                }
                throw new Xunit.Sdk.XunitException("Native invocation completed without entering create authorization.");
            }
            Assert.Same(factory.Entered.Task, reached);
            await factory.Entered.Task;
            Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Ready'"));
            Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationNativeBindings"));
            stop = f.Registry.StopAsync(default);
            try
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(10));
                stoppedBeforeAuthorizationRetired = true;
            }
            catch (TimeoutException) { }
            activeWhileHeld = await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL");
        }
        finally
        {
            factory.Resume.TrySetResult();
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception error) { invocationFailure = error; }
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.False(stoppedBeforeAuthorizationRetired,
            "The actual sealed owner stopped and released its SQLite reservation while its create admission still awaited retirement.");
        Assert.Equal(1L, activeWhileHeld);
        Assert.IsType<WorkflowValidationException>(invocationFailure);
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Terminated'"));
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks", "Phase='Stopped'"));
        Assert.Equal(0L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Db.CountAsync("Sessions", "ActiveExecutionId IS NOT NULL"));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationNativeBindings"));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationTransportChecks", "Phase='Prompt'"));
        // This proves physical native owner lifetime only. Synthetic credentials establish no productive origin.
    }

    private sealed class PausedCreateAuthorizationFactory(ISqliteConnectionFactory inner) : ISqliteConnectionFactory
    {
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection() => inner.CreateConnection();
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return await inner.OpenConnectionAsync(cancellationToken);
        }
    }
}

using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    private sealed class OwnedFixtureSecretStore : ISecretStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _keys = new();
        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = "urn:llmworkgui:secret:" + Guid.NewGuid().ToString("N"); _keys[reference] = secret;
            return Task.FromResult(reference);
        }
        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_keys.GetValueOrDefault(secretReference)); }
        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_keys.TryRemove(secretReference, out _)); }
    }
    private sealed class OwnedRuntimeFixture(TestDatabase db, ServiceProvider services) : IDisposable
    {
        public TestDatabase Db { get; } = db;
        public ServiceProvider Services { get; } = services;
        public OpenCodeAdaptationRuntimeRegistry Registry => Services.GetRequiredService<OpenCodeAdaptationRuntimeRegistry>();
        public IAdaptationEgressPolicy Egress => Services.GetRequiredService<IAdaptationEgressPolicy>();
        public IAdaptationModelInvoker Invoker => Services.GetRequiredService<IAdaptationModelInvoker>();
        public ISecretLifecycleService Secrets => Services.GetRequiredService<ISecretLifecycleService>();
        public AdaptationModelRequest Request => new(new AdaptationRouteIdentity("account-1", "provider-1", BackendType.OpenCode,
            "openai/synthetic-model").RouteId, "openai/synthetic-model", "Owned fixture review", [new("user", "Owned fixture content")])
            { ProjectId = "project-1", SourceVersionId = "material-version" };
        public async Task<AdaptationModelRequest> PrepareAsync()
        { var request = Request; return request with { AdmissionId = await Egress.PrepareAsync(request, default) }; }
        public void Dispose()
        {
            try { Secrets.RevokeAccountSecretAsync("account-1").GetAwaiter().GetResult(); }
            finally { Services.Dispose(); Db.Dispose(); }
        }
    }

    private static async Task<OwnedRuntimeFixture> RuntimeFixture(bool configureMapping = true)
    {
        var db = new TestDatabase(); await SeedMaterial(db);
        var services = new ServiceCollection(); services.AddInfrastructure(db.Root); services.AddWorkflowServices();
        services.AddSingleton<ISqliteConnectionFactory>(db.Factory);
        services.AddSingleton<ISecretStore, OwnedFixtureSecretStore>();
        var built = services.BuildServiceProvider(); var fixture = new OwnedRuntimeFixture(db, built);
        try
        {
            var guard = (ApplicationInstanceGuard)built.GetRequiredService<IApplicationInstanceGuard>();
            await MaterialStore(db, guard).DeclareAsync("material-version", MaterialBlob, 0, DataClassification.PrivateSource);
            await MaterialSql(db, "UPDATE Models SET ProviderModelId='openai/synthetic-model'; UPDATE Accounts SET ProviderNativeId=NULL;");
            var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            await using (var c = await db.Factory.OpenConnectionAsync())
            await using (var command = c.CreateCommand())
            {
                command.CommandText = "UPDATE ProviderProfiles SET ExecutablePath=$exe";
                command.Parameters.AddWithValue("$exe", exe); await command.ExecuteNonQueryAsync();
            }
            await fixture.Secrets.SaveAccountSecretAsync("account-1", "synthetic-owned-runtime-fixture-key");
            if (configureMapping) await fixture.Registry.ConfigureAccountAsync("account-1", "openai");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task<AdaptationModelResponse> PrepareAndInvokeAsync(OwnedRuntimeFixture f)
        => await f.Invoker.InvokeModelAsync(await f.PrepareAsync());

    [Fact]
    public async Task ProductionInvokerRequiresAnExplicitAccountProviderMappingBeforeAnyNativeExecution()
    {
        using var f = await RuntimeFixture(configureMapping: false);
        var request = await f.PrepareAsync();
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Invoker.InvokeModelAsync(request));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners"));
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("foreign/provider")]
    [InlineData("bad\nprovider")]
    [InlineData("../outside")]
    public async Task InvalidConfiguredNativeProviderNeverChangesItsBinding(string provider)
    {
        using var f = await RuntimeFixture();
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.ConfigureAccountAsync("account-1", provider));
        Assert.Equal("openai", await MaterialSql(f.Db, "SELECT NativeProviderId FROM OpenCodeAdaptationAccountMappings"));
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }

    [Fact]
    public async Task NativeLaunchFailureIsReservedThenReleasedOnlyAfterTheActualOwnerStopsAndCanUseCapacityOneAgain()
    {
        using var f = await RuntimeFixture();
        // PowerShell is a real local process but is deliberately not an OpenCode server. No model request occurs.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
            Assert.Equal((long)attempt + 1, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Terminated'"));
            Assert.Equal(0L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
            Assert.Equal(0L, await f.Db.CountAsync("Sessions", "ActiveExecutionId IS NOT NULL"));
        }
        Assert.Equal(4L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks"));
        Assert.Equal(2L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks", "Phase='Stopped'"));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(f.Db.Root, "runs")).Length);
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationNativeBindings"));
    }

    [Fact]
    public async Task DurableReserveAuditFailureRollsBackTheSlotAndRefusesBeforeTheActualSupervisorExecutes()
    {
        using var f = await RuntimeFixture();
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureFailReserve BEFORE INSERT ON WorkflowAdaptationRuntimeChecks WHEN NEW.Phase='Reserve' BEGIN SELECT RAISE(ABORT,'owned synthetic reserve audit failure'); END;");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners"));
        Assert.Equal(0L, await f.Db.CountAsync("Executions"));
        Assert.Equal(0L, await f.Db.CountAsync("Sessions"));
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }

    [Fact]
    public async Task StoppedAuditFailureRetainsTheSlotAndCleanupRetryUsesTheSamePrivateOwnerWithoutAnotherLaunch()
    {
        using var f = await RuntimeFixture();
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureFailStopped BEFORE INSERT ON WorkflowAdaptationRuntimeChecks WHEN NEW.Phase='Stopped' BEGIN SELECT RAISE(ABORT,'owned synthetic stop audit failure'); END;");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Reserved'"));
        Assert.Equal(1L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
        Assert.Equal(1L, await f.Db.CountAsync("Sessions", "ActiveExecutionId IS NOT NULL"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.PrepareAsync());
        await Assert.ThrowsAsync<SqliteException>(() => MaterialSql(f.Db, "UPDATE Executions SET State='Succeeded',EndedAtUtc='2026-10-05T00:00:00Z'"));
        await Assert.ThrowsAsync<SqliteException>(() => MaterialSql(f.Db, "UPDATE Sessions SET ActiveExecutionId=NULL,State='Closed'"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.ConfigureAccountAsync("account-1", "openai"));
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Db.Root, "runs")));
        await MaterialSql(f.Db, "DROP TRIGGER FixtureFailStopped");
        Assert.Equal(0, await f.Registry.RetryOwnedCleanupAsync());
        Assert.Equal(0, await f.Registry.RetryOwnedCleanupAsync());
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Db.Root, "runs")));
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks", "Phase='Stopped'"));
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners", "State='Terminated'"));
        Assert.Equal(0L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
        await f.PrepareAsync();
    }

    [Fact]
    public async Task ANewRegistryCannotReleaseAnotherRegistrysUncertainOwnedReservation()
    {
        using var f = await RuntimeFixture();
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureFailStopped BEFORE INSERT ON WorkflowAdaptationRuntimeChecks WHEN NEW.Phase='Stopped' BEGIN SELECT RAISE(ABORT,'owned synthetic stop audit failure'); END;");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        var other = ActivatorUtilities.CreateInstance<OpenCodeAdaptationRuntimeRegistry>(f.Services);
        await MaterialSql(f.Db, "DROP TRIGGER FixtureFailStopped");
        Assert.Equal(0, await other.RetryOwnedCleanupAsync());
        Assert.Equal(1L, await f.Db.CountAsync("Executions", "EndedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks", "Phase='Stopped'"));
        Assert.Equal(0, await f.Registry.RetryOwnedCleanupAsync());
        Assert.Equal(1L, await f.Db.CountAsync("WorkflowAdaptationRuntimeChecks", "Phase='Stopped'"));
    }

    [Fact]
    public async Task RevokedOrReboundAccountKeyRefusesBeforeAProcessIsCreated()
    {
        using var f = await RuntimeFixture();
        await f.Secrets.RevokeAccountSecretAsync("account-1");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.Equal(0L, await f.Db.CountAsync("WorkflowAdaptationRuntimeOwners"));
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }
}

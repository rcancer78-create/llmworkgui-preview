using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class ModelRouteConfigurationTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private SqliteModelRouteConfigurationService Service(IApplicationInstanceGuard? guard = null) =>
        new(_db.Factory, guard ?? _guard!, new SensitiveDataFilter(), TimeProvider.System);
    private async Task<SqliteModelRouteConfigurationService> Ready()
    {
        await _db.InitializeAsync(); await _db.SeedRouteChainAsync();
        await Execute("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp'; UPDATE Routes SET Backend='CursorAcp'");
        _guard = new ApplicationInstanceGuard(_db.Root);
        return Service();
    }
    private Task<string> AddModel(SqliteModelRouteConfigurationService service, string native = "native-configured") =>
        service.SaveModelAsync(new("provider-1", native, "Настроенная модель", CapabilityState.Supported, true));
    private Task<string> AddRoute(SqliteModelRouteConfigurationService service, string modelId) =>
        service.SaveRouteAsync(new("provider-1", "account-1", modelId, "ask", DataClassification.PrivateSource, true, 1));
    private async Task Execute(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private async Task<object?> Scalar(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task SavedConfiguration_ReopensAndFeedsRealCursorAdmission()
    {
        var service = await Ready(); var modelId = await AddModel(service); var routeId = await AddRoute(service, modelId);
        var reopened = await Service().ReadAsync();
        var model = Assert.Single(reopened.Models.Where(m => m.Id == modelId));
        Assert.Equal(ModelProvenance.UserDefined, model.Provenance);
        Assert.Equal("native-configured", model.NativeModelId);
        Assert.Single(reopened.Routes.Where(r => r.Id == routeId));
        var journal = new SqliteCursorAcpExecutionJournal(_db.Factory, TimeProvider.System, _guard!);
        var route = Assert.Single((await journal.ListRoutesAsync()).Where(r => r.Id == routeId));
        var entry = await journal.BeginAsync("project-1", _db.GetWorkspacePath(), "native-session", route, "ask", "request", "fixture-hash");
        Assert.Equal(modelId, entry.Route.ModelId);
        Assert.Equal(DBNull.Value, await Scalar("SELECT ObservedRouteId FROM Executions"));
        Assert.Equal(DBNull.Value, await Scalar($"SELECT GatewayNativeId FROM Models WHERE Id='{modelId}'"));
        Assert.Equal(DBNull.Value, await Scalar($"SELECT GatewayRouteKey FROM Routes WHERE Id='{routeId}'"));
        await journal.CompleteAsync(entry, new() { SessionId = entry.NativeSessionId, ClientRequestId = entry.ClientRequestId, Outcome = CursorAcpTurnOutcome.Succeeded });
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("C:\\secret")]
    [InlineData("urn:llmworkgui:secret:key")]
    [InlineData("invalid\nmodel")]
    [InlineData("")]
    public async Task InvalidModelIds_AreRefusedWithoutWriting(string native)
    {
        var service = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddModel(service, native));
        Assert.Single((await service.ReadAsync()).Models);
    }

    [Fact]
    public async Task ConcurrentDuplicateModels_OnlyOneRowIsCreated()
    {
        var service = await Ready();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        { await start.Task; return await Record.ExceptionAsync(() => AddModel(service)); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(e => e is null)); Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(e => e is not null)));
        Assert.Equal(2, (await service.ReadAsync()).Models.Count);
    }

    [Fact]
    public async Task ConcurrentDuplicateRoutes_OnlyOneRowIsCreated()
    {
        var service = await Ready(); var model = await AddModel(service);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        { await start.Task; return await Record.ExceptionAsync(() => AddRoute(service, model)); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(e => e is null)); Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(e => e is not null)));
        Assert.Equal(2, (await service.ReadAsync()).Routes.Count);
    }

    [Theory]
    [InlineData("account-absent", "model-1", "ask", DataClassification.PublicSource)]
    [InlineData("account-1", "missing-model", "ask", DataClassification.PublicSource)]
    [InlineData("account-1", "model-1", "invalid-mode", DataClassification.PublicSource)]
    [InlineData("account-1", "model-1", "ask", DataClassification.Restricted)]
    public async Task InvalidRouteRelationsOrPolicy_RefuseWithoutWriting(string account, string model, string mode, DataClassification dataClass)
    {
        var service = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRouteAsync(new("provider-1", account, model, mode, dataClass, true, 0)));
        Assert.Single((await service.ReadAsync()).Routes);
    }

    [Fact]
    public async Task CrossProfileAccountAndModel_AreRefused()
    {
        var service = await Ready();
        await _db.SeedRouteChainAsync("project-2", _db.GetWorkspacePath("second"), "provider-2", "account-2", "model-2", "route-2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRouteAsync(new("provider-1", "account-2", "model-1", "ask", DataClassification.PublicSource, true, 0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRouteAsync(new("provider-1", "account-1", "model-2", "ask", DataClassification.PublicSource, true, 0)));
        Assert.Equal(2, (await service.ReadAsync()).Routes.Count);
    }

    [Fact]
    public async Task StaleEditsCannotOverwriteNewModelOrRouteValues()
    {
        var service = await Ready(); var id = await AddModel(service); var routeId = await AddRoute(service, id);
        var snapshot = await service.ReadAsync(); var model = snapshot.Models.Single(m => m.Id == id); var route = snapshot.Routes.Single(r => r.Id == routeId);
        var modelEdit = new SaveModelConfiguration(model.ProfileId, model.NativeModelId, "Новое имя", model.Capability, false, model);
        await service.SaveModelAsync(modelEdit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveModelAsync(modelEdit with { Name = "Устаревшая правка" }));
        var routeEdit = new SaveRouteConfiguration(route.ProfileId, route.AccountId, route.ModelId, route.Mode, route.MaxDataClass, false, 3, route);
        await service.SaveRouteAsync(routeEdit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRouteAsync(routeEdit with { Priority = 2 }));
        var latest = await service.ReadAsync();
        Assert.Equal("Новое имя", latest.Models.Single(m => m.Id == id).Name);
        Assert.Equal(3, latest.Routes.Single(r => r.Id == routeId).Priority);
    }

    [Fact]
    public async Task UpdatingConfiguration_PreservesHealthAndObservedIdentityColumns()
    {
        var service = await Ready(); var id = await AddModel(service); var routeId = await AddRoute(service, id);
        await Execute($"UPDATE Models SET Health='ProbeRequired',GatewayNativeId='observed-model' WHERE Id='{id}'; UPDATE Routes SET Health='QuarantinedAuto',GatewayRouteKey='observed-route' WHERE Id='{routeId}'");
        var snapshot = await service.ReadAsync(); var model = snapshot.Models.Single(m => m.Id == id); var route = snapshot.Routes.Single(r => r.Id == routeId);
        await service.SaveModelAsync(new(model.ProfileId, model.NativeModelId, "Новое имя", model.Capability, false, model));
        await service.SaveRouteAsync(new(route.ProfileId, route.AccountId, route.ModelId, route.Mode, route.MaxDataClass, false, 4, route));
        Assert.Equal("ProbeRequired", await Scalar($"SELECT Health FROM Models WHERE Id='{id}'"));
        Assert.Equal("observed-model", await Scalar($"SELECT GatewayNativeId FROM Models WHERE Id='{id}'"));
        Assert.Equal("QuarantinedAuto", await Scalar($"SELECT Health FROM Routes WHERE Id='{routeId}'"));
        Assert.Equal("observed-route", await Scalar($"SELECT GatewayRouteKey FROM Routes WHERE Id='{routeId}'"));
    }

    [Fact]
    public async Task BindingChangesAreRefusedAndUnknownAuthorizationIsNotPromoted()
    {
        var service = await Ready(); var id = await AddModel(service); var routeId = await AddRoute(service, id);
        await Execute("UPDATE Accounts SET AuthState='Unknown'");
        var snapshot = await service.ReadAsync(); var model = snapshot.Models.Single(m => m.Id == id); var route = snapshot.Routes.Single(r => r.Id == routeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveModelAsync(new(model.ProfileId, "different", model.Name, model.Capability, true, model)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveRouteAsync(new(route.ProfileId, route.AccountId, route.ModelId, "agent", route.MaxDataClass, true, 0, route)));
        await service.SaveRouteAsync(new(route.ProfileId, route.AccountId, route.ModelId, route.Mode, route.MaxDataClass, false, 0, route));
        Assert.Equal("Unknown", await Scalar("SELECT AuthState FROM Accounts"));
        Assert.Empty(await new SqliteCursorAcpExecutionJournal(_db.Factory, TimeProvider.System, _guard!).ListRoutesAsync());
    }

    [Fact]
    public async Task SavedModelsReachProviderCatalog_AndDisabledOverrideIsRespected()
    {
        var service = await Ready(); var id = await AddModel(service);
        var catalog = new ApplicationSettingsProviderModelCatalog(new SqliteApplicationSettingsRepository(_db.Factory), new SensitiveDataFilter(), _db.Factory);
        var query = new ProviderModelCatalogQuery("provider-1", BackendType.CursorAcp);
        await catalog.SaveModelsAsync(query, [new("native-configured", "Старое имя")]);
        Assert.Contains(await catalog.ListModelsAsync(query), m => m.ModelId == "native-configured" && m.DisplayName == "Настроенная модель");
        var model = (await service.ReadAsync()).Models.Single(m => m.Id == id);
        await service.SaveModelAsync(new(model.ProfileId, model.NativeModelId, model.Name, model.Capability, false, model));
        Assert.DoesNotContain(await catalog.ListModelsAsync(query), m => m.ModelId == "native-configured");
    }

    [Fact]
    public async Task SecondaryInstance_CanReadButCannotWrite()
    {
        await Ready(); var service = Service(new ViewOnlyGuard());
        Assert.Single((await service.ReadAsync()).Models);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddModel(service));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AddRoute(service, "model-1"));
    }
    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View-only");
        public void Dispose() { }
    }
}

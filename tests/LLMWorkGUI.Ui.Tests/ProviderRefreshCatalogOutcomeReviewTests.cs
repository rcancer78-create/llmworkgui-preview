using System.IO;
using System.Net;
using System.Net.Http;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ProviderRefreshCatalogOutcomeReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyASuccessfulEmptyEndpointInventoryCanErasePreviouslyPersistedModels(bool successfulEmpty)
    {
        StaTestRunner.EnsureApplication();
        var root = Directory.CreateTempSubdirectory("LLMWorkGUI-owned-refresh-catalog-");
        var factory = new SqliteConnectionFactory(Path.Combine(root.FullName, "owned.db"));
        using var guard = new ApplicationInstanceGuard(root.FullName);
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            var profiles = new SqliteProviderProfileRepository(factory, guard);
            await profiles.UpsertAsync(new ProviderProfile("owned-refresh-provider", "Owned refresh",
                BackendType.OpenCode, "http://127.0.0.1:1/v1", null, DataClassification.PublicSource, true));
            var catalog = new ApplicationSettingsProviderModelCatalog(new SqliteApplicationSettingsRepository(factory));
            var query = new ProviderModelCatalogQuery("owned-refresh-provider", BackendType.OpenCode);
            await catalog.SaveModelsAsync(query, [new ProviderModelDescriptor("owned-existing-model", "Owned existing model")]);
            Assert.Equal("owned-existing-model", Assert.Single(await catalog.ListModelsAsync(query)).ModelId);

            using var transport = new EndpointReply(successfulEmpty);
            using var http = new HttpClient(transport);
            var connection = new ProviderConnectionTestService(httpClient: http);
            var refresh = new ModelRefreshService(connection);
            await StaTestRunner.Run(async () =>
            {
                var model = new ProvidersAccountsViewModel(
                    new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                    profileRepository: profiles, modelRefreshService: refresh,
                    modelCatalogWriter: catalog, instanceGuard: guard);
                await model.InitializeAsync();
                model.SelectedProvider = Assert.Single(model.Providers);
                Assert.True(model.RefreshModelsCommand.CanExecute(null));
                await model.ExecuteRefreshModelsAsync();
                Assert.False(model.IsBusy);
                Assert.Equal(1, transport.Requests);
                Assert.True(model.ConfirmAndSaveCommand.CanExecute(null));
                await model.ExecuteConfirmAndSaveAsync();
                var persisted = await profiles.GetByIdAsync("owned-refresh-provider");
                Assert.NotNull(persisted);
                Assert.Equal("http://127.0.0.1:1/v1", persisted!.BaseUrl);
            });
            // Read through a new production catalog instance: the assertion concerns durable SQL,
            // not the editor's temporary collection or a fabricated failed discovery DTO.
            var reloaded = new ApplicationSettingsProviderModelCatalog(new SqliteApplicationSettingsRepository(factory));
            var models = await reloaded.ListModelsAsync(query);
            if (successfulEmpty) Assert.Empty(models);
            else Assert.Equal("owned-existing-model", Assert.Single(models).ModelId);
        }
        finally
        {
            guard.Dispose();
            using var connection = factory.CreateConnection();
            SqliteConnection.ClearPool(connection);
            root.Delete(true);
        }
    }

    private sealed class EndpointReply(bool successfulEmpty) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(successfulEmpty ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(successfulEmpty ? "{\"data\":[]}" : "{\"error\":\"owned unavailable\"}")
            });
        }
    }
}

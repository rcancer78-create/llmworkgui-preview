using System.IO;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ProviderOperationOutcomeReviewTests
{
    [Fact]
    public async Task HeldInitializationReservesPublicMutationsAndRestoresTheirAdmissionAfterCompletion()
    {
        StaTestRunner.EnsureApplication();
        using var world = await World.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new Profiles(world.Profiles)
        {
            List = async () => { entered.TrySetResult(); await release.Task; return await world.Profiles.ListAsync(); }
        };
        await StaTestRunner.Run(async () =>
        {
            var model = world.Model(profiles);
            var loading = model.InitializeAsync();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(model.IsBusy);
                Assert.False(model.NewProviderCommand.CanExecute(null));
                Assert.False(model.ConfirmAndSaveCommand.CanExecute(null));
                Assert.False(model.AddApprovalRuleCommand.CanExecute(null));
                model.NewProviderCommand.Execute(null);
                Assert.Equal("owned-provider", model.EditingProviderId);
            }
            finally { release.TrySetResult(); await loading.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.False(model.IsBusy);
            Assert.True(model.NewProviderCommand.CanExecute(null));
            Assert.True(model.ConfirmAndSaveCommand.CanExecute(null));
            Assert.Equal("owned-provider", Assert.Single(model.Providers).Id);
        });
    }

    [Fact]
    public async Task FaultedConnectionTestReplacesItsInProgressIndicatorWithARedactedTerminalFailure()
    {
        StaTestRunner.EnsureApplication();
        using var world = await World.CreateAsync();
        await StaTestRunner.Run(async () =>
        {
            var model = world.Model(connection: new FaultingConnection());
            await model.ExecuteTestConnectionAsync();
            Assert.False(model.IsBusy);
            Assert.NotNull(model.ConnectionTestStatus);
            Assert.NotEqual(ProviderConnectionStatus.Success, model.ConnectionTestStatus);
            Assert.False(string.IsNullOrWhiteSpace(model.ConnectionTestMessage));
            Assert.DoesNotContain("Проверка подключения...", model.ConnectionTestMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("owned-credential-canary", model.ConnectionTestMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("owned-credential-canary", model.StatusMessage ?? string.Empty, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommittedApprovalRuleMutationIsReportedTruthfullyWhenOnlyTheFollowingReloadFails(bool remove)
    {
        StaTestRunner.EnsureApplication();
        using var world = await World.CreateAsync();
        var seed = new ApprovalRule("owned-rule", BackendType.OpenCode, "owned-provider", "owned-project",
            null, NormalizedApprovalKind.ReadFile, "read", null, "owned-fixture", DateTimeOffset.UtcNow);
        if (remove) await world.Rules.UpsertAsync(seed);
        var rules = new FaultingRuleReload(world.Rules);
        await StaTestRunner.Run(async () =>
        {
            var model = world.Model(rules: rules);
            if (remove) model.SelectedApprovalRule = new ApprovalRuleItemViewModel(seed);
            if (remove) await model.ExecuteRemoveApprovalRuleAsync(model.SelectedApprovalRule);
            else await model.ExecuteAddApprovalRuleAsync();

            var persisted = await world.Rules.ListAsync();
            if (remove) Assert.Empty(persisted);
            else Assert.Equal("read-owned", Assert.Single(persisted).Operation);
            Assert.Equal(1, rules.Writes);
            Assert.Equal(1, rules.ReloadAttempts);
            Assert.False(model.IsBusy);
            Assert.DoesNotContain("Не удалось сохранить", model.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Не удалось удалить", model.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(remove ? "удален" : "добавлен", model.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (remove) Assert.Null(model.SelectedApprovalRule);
        });
    }

    private sealed class World : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("LLMWorkGUI-owned-provider-outcome-");
        private readonly ApplicationInstanceGuard _guard;
        private readonly SqliteConnectionFactory _factory;
        public SqliteProviderProfileRepository Profiles { get; }
        public SqliteProjectRepository Projects { get; }
        public SqliteApprovalRuleRepository Rules { get; }
        private World()
        {
            _guard = new(_root.FullName);
            _factory = new(Path.Combine(_root.FullName, "owned.db"));
            Profiles = new(_factory, _guard); Projects = new(_factory); Rules = new(_factory, _guard);
        }
        public static async Task<World> CreateAsync()
        {
            var world = new World();
            try
            {
                await new DatabaseMigrator(world._factory).MigrateAsync();
                await world.Profiles.UpsertAsync(new ProviderProfile("owned-provider", "Owned", BackendType.OpenCode,
                    "https://owned.invalid/v1", null, DataClassification.PublicSource, true));
                await world.Projects.UpsertAsync(new Project("owned-project", "Owned", world._root.FullName,
                    null, false, false, null, null, DataClassification.PublicSource));
                return world;
            }
            catch { world.Dispose(); throw; }
        }
        public ProvidersAccountsViewModel Model(IProviderProfileRepository? profiles = null,
            IApprovalRuleRepository? rules = null, IProviderConnectionTestService? connection = null) => new(
                new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: profiles ?? Profiles, approvalRuleRepository: rules ?? Rules,
                connectionTestService: connection, projectRepository: Projects, instanceGuard: _guard)
            {
                EditingProviderId = "owned-provider", EditingDisplayName = "Owned", EditingBaseUrl = "https://owned.invalid/v1",
                NewRuleProjectId = "owned-project", NewRuleOperation = "read-owned", NewRuleKind = NormalizedApprovalKind.ReadFile
            };
        public void Dispose()
        {
            _guard.Dispose();
            using var connection = _factory.CreateConnection(); SqliteConnection.ClearPool(connection);
            _root.Delete(true);
        }
    }
    private sealed class Profiles(IProviderProfileRepository inner) : IProviderProfileRepository
    {
        public Func<Task<IReadOnlyList<ProviderProfile>>>? List;
        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken token = default) => List?.Invoke() ?? inner.ListAsync(token);
        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken token = default) => inner.GetByIdAsync(id, token);
        public Task UpsertAsync(ProviderProfile profile, string? secret = null, CancellationToken token = default) => inner.UpsertAsync(profile, secret, token);
        public Task<long> UpsertReturningRevisionAsync(ProviderProfile profile, string? secret = null, CancellationToken token = default) => inner.UpsertReturningRevisionAsync(profile, secret, token);
        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken token = default) => inner.GetApiKeySecretReferenceAsync(id, token);
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id, token);
    }
    private sealed class FaultingConnection : IProviderConnectionTestService
    {
        public Task<ProviderConnectionTestResult> TestConnectionAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, TimeSpan? timeout = null, CancellationToken token = default) =>
            Task.FromException<ProviderConnectionTestResult>(new IOException("owned-credential-canary"));
    }
    private sealed class FaultingRuleReload(IApprovalRuleRepository inner) : IApprovalRuleRepository
    {
        public int Writes;
        public int ReloadAttempts;
        public Task<IReadOnlyList<ApprovalRule>> ListAsync(CancellationToken token = default)
        { ReloadAttempts++; return Task.FromException<IReadOnlyList<ApprovalRule>>(new IOException("owned reload-only failure")); }
        public Task<IReadOnlyList<ApprovalRule>> ListByProjectIdAsync(string id, CancellationToken token = default) => inner.ListByProjectIdAsync(id, token);
        public Task<ApprovalRule?> GetByIdAsync(string id, CancellationToken token = default) => inner.GetByIdAsync(id, token);
        public async Task UpsertAsync(ApprovalRule rule, CancellationToken token = default) { await inner.UpsertAsync(rule, token); Writes++; }
        public async Task<bool> DeleteAsync(string id, CancellationToken token = default) { var result = await inner.DeleteAsync(id, token); Writes++; return result; }
    }
}

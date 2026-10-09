using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ApprovalRuleUiAuthorityReviewRegressionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ApprovalRuleWritesRespectActualInstanceAuthority(bool viewOnly, bool remove)
    {
        var root = Directory.CreateTempSubdirectory("LLMWorkGUI-owned-approval-ui-");
        ISqliteConnectionFactory? factory = null;
        try
        {
            var guard = new Guard();
            var services = new ServiceCollection();
            services.AddSingleton<IApplicationInstanceGuard>(guard);
            services.AddInfrastructure(root.FullName);
            using var provider = services.BuildServiceProvider();
            factory = provider.GetRequiredService<ISqliteConnectionFactory>();
            await new DatabaseMigrator(factory).MigrateAsync();
            var profiles = provider.GetRequiredService<IProviderProfileRepository>();
            var projects = provider.GetRequiredService<IProjectRepository>();
            var rules = provider.GetRequiredService<IApprovalRuleRepository>();
            await profiles.UpsertAsync(new ProviderProfile("owned-provider", "Owned fixture", BackendType.OpenCode,
                "https://fixture.invalid/v1", null, DataClassification.PublicSource, true));
            await projects.UpsertAsync(new Project("owned-project", "Owned fixture", root.FullName, null,
                false, false, null, null, DataClassification.PublicSource));
            var seed = new ApprovalRule("owned-seed", BackendType.OpenCode, "owned-provider", "owned-project",
                null, NormalizedApprovalKind.ReadFile, "read", null, "owned-fixture", DateTimeOffset.UtcNow);
            await rules.UpsertAsync(seed);
            guard.IsViewOnly = viewOnly; // Seed under primary authority; all tested writes run under the selected authority.
            var viewModel = new ProvidersAccountsViewModel(
                new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: profiles, approvalRuleRepository: rules, projectRepository: projects, instanceGuard: guard)
            {
                EditingProviderId = "owned-provider", NewRuleProjectId = "owned-project",
                NewRuleKind = NormalizedApprovalKind.ReadFile, NewRuleOperation = "read-new",
                NewRuleBackend = BackendType.OpenCode, SelectedApprovalRule = new ApprovalRuleItemViewModel(seed)
            };

            if (remove) await viewModel.ExecuteRemoveApprovalRuleAsync(viewModel.SelectedApprovalRule);
            else await viewModel.ExecuteAddApprovalRuleAsync();

            var persisted = await rules.ListAsync();
            if (viewOnly)
            {
                Assert.Equal("owned-seed", Assert.Single(persisted).Id);
                Assert.False(viewModel.AddApprovalRuleCommand.CanExecute(null));
                Assert.False(viewModel.RemoveApprovalRuleCommand.CanExecute(null));
            }
            else if (remove) Assert.Empty(persisted);
            else
            {
                Assert.Equal(2, persisted.Count);
                Assert.Single(persisted.Where(rule => rule.Operation == "read-new"));
            }
        }
        finally
        {
            if (factory is not null)
            {
                using var pool = factory.CreateConnection();
                SqliteConnection.ClearPool(pool);
            }
            root.Delete(recursive: true);
        }
    }

    private sealed class Guard : IApplicationInstanceGuard
    {
        public string InstanceId => "owned-approval-ui-instance";
        public bool IsViewOnly { get; set; }
        public bool IsPrimarySupervisor => !IsViewOnly;
        public void EnsureSupervisorPermitted()
        {
            if (IsViewOnly) throw new InvalidOperationException("Owned fixture is view-only.");
        }
        public void Dispose() { }
    }
}

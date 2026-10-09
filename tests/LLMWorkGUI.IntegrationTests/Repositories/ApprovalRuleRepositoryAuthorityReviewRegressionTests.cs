using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class ApprovalRuleRepositoryAuthorityReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectViewOnlyWriteIsRefusedBeforeChangingTheActualApprovalTable(bool delete)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var guard = new Guard();
        var services = new ServiceCollection();
        services.AddSingleton<IApplicationInstanceGuard>(guard);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(database.Factory.DatabasePath, provider.GetRequiredService<ISqliteConnectionFactory>().DatabasePath);
        var rules = provider.GetRequiredService<IApprovalRuleRepository>();
        var original = Rule("original");
        await rules.UpsertAsync(original);
        guard.IsViewOnly = true;

        var failure = delete
            ? await Record.ExceptionAsync(async () => { await rules.DeleteAsync(original.Id); })
            : await Record.ExceptionAsync(() => rules.UpsertAsync(Rule("unauthorized replacement")));

        var retained = Assert.Single(await rules.ListAsync());
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal("original", retained.Operation);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, guard.RefusedWrites);
    }

    private static ApprovalRule Rule(string operation) => new("owned-approval", BackendType.OpenCode,
        "provider-1", "project-1", null, NormalizedApprovalKind.ReadFile, operation, null,
        "owned-fixture", DateTimeOffset.UtcNow);

    private sealed class Guard : IApplicationInstanceGuard
    {
        public string InstanceId => "owned-approval-repository-instance";
        public bool IsViewOnly { get; set; }
        public bool IsPrimarySupervisor => !IsViewOnly;
        public int RefusedWrites { get; private set; }
        public void EnsureSupervisorPermitted()
        {
            if (!IsViewOnly) return;
            RefusedWrites++;
            throw new InvalidOperationException("Owned fixture is view-only.");
        }
        public void Dispose() { }
    }
}

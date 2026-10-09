using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class CursorBackendHealthAdmissionReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedBackendExclusionBlocksPreviouslySelectedCursorRouteAndAdmission(bool inspectInventory)
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        await db.SeedRouteChainAsync();
        await using (var connection = await db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp',ProviderModelId='native-model'; UPDATE Routes SET Backend='CursorAcp'";
            await command.ExecuteNonQueryAsync();
        }
        using var guard = new ApplicationInstanceGuard(Path.Combine(db.Root, "instance"));
        var journal = new SqliteCursorAcpExecutionJournal(db.Factory, TimeProvider.System, guard);
        var selected = Assert.Single(await journal.ListRoutesAsync());
        var health = new HealthCenterService(new SqliteHealthStateRepository(db.Factory),
            new SqliteHealthEventRepository(db.Factory), instanceGuard: guard,
            transitionStore: new SqliteHealthTransitionStore(db.Factory));
        await health.DisableManuallyAsync(HealthScope.ForBackend("cursor-agent-acp"), "owned regression exclusion");
        Assert.False((await health.GetSnapshotAsync(HealthScope.ForBackend("cursor-agent-acp"))).IsRoutable);
        if (inspectInventory)
            Assert.Empty(await journal.ListRoutesAsync());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync("project-1", db.GetWorkspacePath(),
                "native-session", selected, "ask", Guid.NewGuid().ToString("D"), "synthetic-prompt-hash"));
        await health.EnableAsync(HealthScope.ForBackend("cursor-agent-acp"), "owned recovery control");
        await health.ForceEnableAsync(HealthScope.ForBackend("cursor-agent-acp"), "explicit unverified recovery control");
        Assert.Single(await journal.ListRoutesAsync());
        var fresh = await journal.BeginAsync("project-1", db.GetWorkspacePath(), "native-session", selected,
            "ask", Guid.NewGuid().ToString("D"), "synthetic-prompt-hash");
        Assert.NotNull(fresh);
    }
}

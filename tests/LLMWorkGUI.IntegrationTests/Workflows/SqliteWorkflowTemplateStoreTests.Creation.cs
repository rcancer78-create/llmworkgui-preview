using LLMWorkGUI.Application.Workflows.Studio;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class SqliteWorkflowTemplateStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudioCreationReturnsOnlyAfterTheNewVersionIsDurable(bool newVersion)
    {
        var factory = await CreateMigratedFactoryAsync();
        var studio = new WorkflowStudioService(templateStore: CreateStore(factory));
        if (newVersion)
            await studio.SaveTemplateAsync(studio.GetBuiltInTemplates()[0].Clone("durable-template", "Source"));
        var created = newVersion
            ? await studio.CreateTemplateVersionAsync("durable-template", 2)
            : await studio.CloneTemplateAsync(WorkflowStudioService.StandardTemplateId, "durable-template", "Clone");
        TestSqlitePool.Clear(factory);
        var restarted = new WorkflowStudioService(templateStore: CreateStore(CreateFactory()));
        var restored = await restarted.GetRequiredTemplateAsync(created.TemplateId, created.Version);
        Assert.Equal(created.DisplayName, restored.DisplayName);
        Assert.Equal(created.Graph.Nodes.Select(n => n.NodeId), restored.Graph.Nodes.Select(n => n.NodeId));
        Assert.Equal(newVersion ? 2 : 1, restored.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StudioCreationPropagatesStorageFailureWithoutReturningAnUnsavedSuccess(bool newVersion)
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);
        var studio = new WorkflowStudioService(templateStore: store);
        if (newVersion)
            await studio.SaveTemplateAsync(studio.GetBuiltInTemplates()[0].Clone("failed-template", "Source"));
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectTemplateCreation BEFORE INSERT ON WorkflowTemplateVersions
                WHEN NEW.TemplateId='failed-template'
                BEGIN SELECT RAISE(ABORT, 'injected template write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => newVersion
            ? studio.CreateTemplateVersionAsync("failed-template", 2)
            : studio.CloneTemplateAsync(WorkflowStudioService.StandardTemplateId, "failed-template", "Clone"));
        Assert.Null(await store.GetAsync("failed-template", newVersion ? 2 : 1));
        if (newVersion) Assert.NotNull(await store.GetAsync("failed-template", 1));
    }
}

using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class SqliteWorkflowTemplateStoreTests
{
    [Fact]
    public async Task Review_AnInvalidSuppliedSeedCannotPersistAnyImmutableSeedRow()
    {
        var factory = await CreateMigratedFactoryAsync();
        var valid = CreateTemplate("valid-seed", 1);
        var invalid = new WorkflowTemplateDefinition("invalid-seed", 1, "Invalid seed", "Injected invalid graph",
            new WorkflowGraph("missing-entry", valid.Graph.Nodes), valid.RoleBindings,
            valid.RequiredDocumentTemplates, true, Now);
        var store = new SqliteWorkflowTemplateStore(factory, seedVersions: new[] { valid, invalid });

        await Assert.ThrowsAsync<WorkflowValidationException>(() => store.ListAsync());

        await using var connection = await factory.OpenConnectionAsync();
        Assert.Equal("0", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateVersions;"));
        // The current shipped default is valid; this is the public supplied-seed storage contract.
    }

    [Fact]
    public async Task Review_AFailureWritingALaterSeedRollsBackTheEntireImmutableSeedBatch()
    {
        var factory = await CreateMigratedFactoryAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectLaterSeed BEFORE INSERT ON WorkflowTemplateVersions
                WHEN NEW.TemplateId='later-seed'
                BEGIN SELECT RAISE(ABORT, 'synthetic seed write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteWorkflowTemplateStore(factory,
            seedVersions: new[] { CreateTemplate("first-seed", 1), CreateTemplate("later-seed", 1) });

        await Assert.ThrowsAsync<SqliteException>(() => store.ListAsync());

        await using var reopened = await factory.OpenConnectionAsync();
        Assert.Equal("0", await ReadSingleAsync(reopened, "SELECT COUNT(*) FROM WorkflowTemplateVersions;"));
        await using (var drop = reopened.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER RejectLaterSeed;";
            await drop.ExecuteNonQueryAsync();
        }
        Assert.Equal(2, (await store.ListAsync()).Count);
    }
}

using Xunit;
namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowActivationServiceTests
{
    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[42]")]
    [InlineData("{\"Executor\":42}")]
    [InlineData("{\"unknown-role\":\"model\"}")]
    public async Task InvalidBindingsWithNoDeclaredRolesCannotBeAcknowledged(string bindings)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("invalid-bindings",
            CreateArchive(("README.md", "workflow")), declaredRolesJson: "[]", bindingsJson: bindings);
        var validation = await _service.ValidateForActivationAsync(version.Id);
        Assert.True(validation.HasUnclearableBlockers);
        Assert.Contains(validation.Issues, issue => issue.IsNotClearable);
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
    }
}

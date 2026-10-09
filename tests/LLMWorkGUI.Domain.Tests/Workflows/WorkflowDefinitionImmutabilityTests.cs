using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

public sealed class WorkflowDefinitionImmutabilityTests
{
    [Fact]
    public void GraphEnumerationCannotDivergeFromItsNodeLookup()
    {
        var original = Node("terminal");
        var graph = new WorkflowGraph("terminal", new[] { original });
        TryReplace(graph.Nodes, Node("replacement"));

        Assert.Same(original, Assert.Single(graph.Nodes));
        Assert.Same(original, graph.GetRequiredNode("terminal"));
        graph.Validate();
    }

    [Fact]
    public void TemplateCannotReplaceARoleBindingWithoutANewVersion()
    {
        var original = new RoleBindingDefinition("Role", "original-route");
        var template = Template(original);
        TryReplace(template.RoleBindings, new RoleBindingDefinition("Role", "replacement-route"));

        Assert.Same(original, Assert.Single(template.RoleBindings));
    }

    [Fact]
    public void TemplateCannotReplaceItsRequiredDocumentPolicyWithoutANewVersion()
    {
        var template = Template(new RoleBindingDefinition("Role", "route"));
        TryReplace(template.RequiredDocumentTemplates, DocumentTemplateKind.AcceptanceReport);

        Assert.Equal(DocumentTemplateKind.ProblemStatement, Assert.Single(template.RequiredDocumentTemplates));
    }

    [Fact]
    public void ExposedRouteAllowlistCannotAuthorizeAnUndeclaredRoute()
    {
        var binding = new RoleBindingDefinition("Role", "original-route");
        TryReplace(binding.AllowedRouteIds, "undeclared-route");

        Assert.False(binding.AllowsRoute("undeclared-route"));
        Assert.True(binding.AllowsRoute("original-route"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExposedReviewerPolicyCannotReplaceTheRequiredRole(bool nodeGate)
    {
        var roles = nodeGate
            ? new WorkflowNodeGateMetadata(WorkflowStageKind.DocumentReview,
                new[] { "Reviewer" }, false, "Document").RequiredReviewerRoles
            : new WorkflowStageDefinition("stage", "Stage", "Role", WorkflowStageKind.DocumentReview,
                new[] { "Reviewer" }, false, "Document", "next", null).RequiredReviewerRoles;
        TryReplace(roles, "OptionalObserver");

        Assert.Equal("Reviewer", Assert.Single(roles));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NodeCollectionsCannotChangeCapabilityOrFallbackRequirements(bool capabilities)
    {
        var node = new WorkflowNodeDefinition("node", WorkflowNodeKind.Prompt, "Node", "Role",
            requiredCapabilities: new[] { "read-only" }, fallbackRouteIds: new[] { "fallback" });
        var values = capabilities ? node.RequiredCapabilities : node.FallbackRouteIds;
        TryReplace(values, "replacement");

        Assert.Equal(capabilities ? "read-only" : "fallback", Assert.Single(values));
    }

    private static void TryReplace<T>(IReadOnlyList<T> values, T replacement)
    {
        if (values is IList<T> mutable)
        {
            try { mutable[0] = replacement; }
            catch (NotSupportedException) { }
        }
    }

    private static WorkflowNodeDefinition Node(string id) =>
        new(id, WorkflowNodeKind.TerminalOutcome, id, "Role");

    private static WorkflowTemplateDefinition Template(RoleBindingDefinition role) => new(
        "template", 1, "Template", "Immutable definition", new WorkflowGraph("terminal", new[] { Node("terminal") }),
        new[] { role }, new[] { DocumentTemplateKind.ProblemStatement }, false,
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
}

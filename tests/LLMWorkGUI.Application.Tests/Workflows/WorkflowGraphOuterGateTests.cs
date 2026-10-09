using System.Text.Json;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowGraphOuterGateTests
{
    [Theory]
    [InlineData(false, "gateMetadata")]
    [InlineData(false, "GateMetadata")]
    [InlineData(true, "gateMetadata")]
    [InlineData(true, "GateMetadata")]
    public void DuplicatedOuterGateCannotEraseFirstDeclaredPolicy(bool stored, string duplicateName)
    {
        var json = WorkflowGraphSnapshot.Serialize(new WorkflowGraph("start", new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeKind.Prompt, "Start", "Worker",
                successTargetNodeId: "done", gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.Custom, new[] { "Reviewer" }, true, "Evidence")),
            new WorkflowNodeDefinition("done", WorkflowNodeKind.TerminalOutcome, "Done", "Worker")
        }));
        using var canonical = JsonDocument.Parse(json);
        var gate = canonical.RootElement.GetProperty("nodes")[0].GetProperty("gateMetadata").GetRawText();
        var declared = "\"gateMetadata\":" + gate;
        var ambiguous = json.Replace(declared, declared + ",\"" + duplicateName + "\":null", StringComparison.Ordinal);
        using var duplicate = JsonDocument.Parse(ambiguous);
        Assert.Equal(2, duplicate.RootElement.GetProperty("nodes")[0].EnumerateObject()
            .Count(property => string.Equals(property.Name, "gateMetadata", StringComparison.OrdinalIgnoreCase)));
        Assert.Throws<WorkflowValidationException>(() => stored
            ? WorkflowGraphSnapshot.ReadStoredTemplateGraph(ambiguous, "custom-template", 1)
            : WorkflowGraphSnapshot.Deserialize(ambiguous, "pinned-run"));
    }
}

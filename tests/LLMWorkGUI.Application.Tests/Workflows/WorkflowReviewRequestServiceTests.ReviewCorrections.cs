using System.Text;
using LLMWorkGUI.Application.Workflows.Orchestration;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowReviewRequestServiceTests
{
    [Fact]
    public async Task Review_EncodedReplacementCharacterIsPreservedAsRealUtf8Content()
    {
        // This supported channel is an explicit test double. The shipped catalog still refuses
        // generic native review routes; this exercises the content decoder contract alone.
        const string text = "The document intentionally contains U+FFFD: \uFFFD.";
        var channel = new StubChannel();
        var service = CreateService(channel: channel);
        var runId = await StartRunAsync(RealRouteId, content: Encoding.UTF8.GetBytes(text));

        var result = await service.RequestAssignedReviewAsync(runId);

        Assert.Single(result.Roles);
        Assert.Equal(WorkflowReviewRequestOutcome.Dispatched, result.Outcome);
        Assert.Contains(text, Assert.Single(channel.Requests).PromptOrCommand!, StringComparison.Ordinal);
    }
}

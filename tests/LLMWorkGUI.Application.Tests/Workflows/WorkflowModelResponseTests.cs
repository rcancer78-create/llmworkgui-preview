using LLMWorkGUI.Application.Workflows.Declarative;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowModelResponseTests
{
    [Fact]
    public void BoundsAreUtf8BytesAndDiagnosticStringDoesNotContainAnswer()
    {
        Assert.Equal(65536, new WorkflowModelResponse(new string('x', 65536)).Utf8Bytes);
        Assert.Throws<ArgumentException>(() => new WorkflowModelResponse(new string('x', 65537)));
        Assert.Throws<ArgumentException>(() => new WorkflowModelResponse(new string('я', 32769)));
        Assert.ThrowsAny<ArgumentException>(() => new WorkflowModelResponse("\ud800"));
        var response = new WorkflowModelResponse("private-answer-body");
        Assert.DoesNotContain(response.Content, response.ToString());
        Assert.Equal(new WorkflowModelResponse(response.Content).Sha256, response.Sha256);
    }
}

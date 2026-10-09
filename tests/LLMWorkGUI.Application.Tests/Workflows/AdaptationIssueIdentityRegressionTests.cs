using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class AdaptationIssueIdentityRegressionTests
{
    [Theory]
    [InlineData("x|y", "z", "x", "y|z")]
    [InlineData(null, "message", "-", "message")]
    public void AcknowledgementCannotMoveBetweenDistinctIssues(string? roleA, string messageA,
        string? roleB, string messageB)
    {
        var approved = new AdaptationValidationIssue(AdaptationBlockerKind.DisallowedSemanticChange, roleA, messageA);
        var changed = new AdaptationValidationIssue(AdaptationBlockerKind.DisallowedSemanticChange, roleB, messageB);
        Assert.True(new WorkflowActivationValidationResult([approved]).IsFullyAcknowledgedBy([approved]));
        Assert.False(new WorkflowActivationValidationResult([changed]).IsFullyAcknowledgedBy([approved]));
        Assert.NotEqual(approved.Identity, changed.Identity);
    }
}

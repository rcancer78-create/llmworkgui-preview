using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Providers;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowValueInvariantTests
{
    [Fact]
    public void UnknownModelOption_IsNotReportedAsSupported()
    {
        var model = ModelCapabilityDetector.DetectCapabilities("synthetic-model");
        Assert.False(ModelCapabilityDetector.IsOptionSupported(model, "reasoning_efffort"));
        Assert.Throws<ArgumentException>(() => ModelCapabilityDetector.ValidateOptionSupported(model, "reasoning_efffort"));
    }

    [Theory]
    [InlineData("User", "user")]
    [InlineData("ASSISTANT", "assistant")]
    public void AdaptationTurn_StoresCanonicalWireRole(string input, string expected)
    {
        Assert.Equal(expected, new AdaptationTurnMessage(input, "content").Role);
    }

    [Fact]
    public void SecretScanReport_CannotHideSuppliedFindings()
    {
        var report = new WorkflowSecretScanReport(false,
            new[] { new WorkflowSecretFinding("example.txt", 1, "secret", "[REDACTED]") },
            Array.Empty<string>());

        Assert.True(report.HasFindings);
    }
}

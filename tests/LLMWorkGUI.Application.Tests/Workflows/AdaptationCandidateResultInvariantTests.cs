using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class AdaptationCandidateResultInvariantTests
{
    [Theory]
    [InlineData("issues")]
    [InlineData("references")]
    [InlineData("semantic")]
    [InlineData("secrets")]
    [InlineData("parse")]
    public void DetailedFailureCannotBeHiddenByAnEmptySummary(string failure)
    {
        var issue = new AdaptationValidationIssue(AdaptationBlockerKind.DisallowedSemanticChange,
            null, "The candidate has a semantic blocker.");
        var issues = new[] { issue };
        var result = new AdaptationCandidateResult("session", "version", 1, "route", "model",
            AdaptationGoal.Balanced, false, @"D:\scratch", Array.Empty<SemanticRoleMapping>(),
            "rationale", Array.Empty<string>(), Array.Empty<AdaptationBlockerKind>(),
            failure == "issues" ? issues : Array.Empty<AdaptationValidationIssue>(),
            failure == "secrets"
                ? new WorkflowSecretScanReport(true, Array.Empty<WorkflowSecretFinding>(), Array.Empty<string>())
                : WorkflowSecretScanReport.Empty,
            failure == "references" ? new AdaptationReferenceValidationResult(issues)
                : AdaptationReferenceValidationResult.Empty,
            new SemanticDiffResult(failure == "semantic" ? issues : Array.Empty<AdaptationValidationIssue>(),
                Array.Empty<string>(), failure == "semantic"),
            new WorkflowPackageDiff(Array.Empty<WorkflowFileDiff>()), new Dictionary<string, string>(),
            Array.Empty<string>(), failure == "parse" ? "Malformed response" : null);

        Assert.True(result.HasBlockers);
    }
}

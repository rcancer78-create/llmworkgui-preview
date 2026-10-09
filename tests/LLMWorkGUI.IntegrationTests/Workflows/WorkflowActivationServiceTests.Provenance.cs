using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowActivationServiceTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"sourceVersionId\":null}")]
    [InlineData("{\"sourceVersionId\":42}")]
    [InlineData("{\"sourceVersionId\":\" \"}")]
    [InlineData("{\"sourceVersionId\":\"version-candidate\",\"sourceVersionId\":\"missing\"}")]
    [InlineData("{\"SourceVersionId\":\"missing\"}")]
    public async Task ValidateForActivationAsync_InvalidProvenanceCannotBecomeAnAbsentBaseline(string report)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, candidate) = await SeedWorkflowAsync("version-candidate",
            CreateArchive(("README.md", "# Workflow")));
        await ReplaceStoredVersionAsync(candidate, candidate.BlobId, candidate.OriginalHash, report);

        var validation = await _service.ValidateForActivationAsync(candidate.Id);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue =>
            issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange && issue.IsNotClearable);
        Assert.False(validation.IsFullyAcknowledgedBy(validation.Issues));
        Assert.Empty(GetAdaptationScratchDirectories());
    }
}

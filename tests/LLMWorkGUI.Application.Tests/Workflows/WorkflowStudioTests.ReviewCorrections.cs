using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowStudioTests
{
    [Fact]
    public async Task Review_LatestLookupIncludesStoredVersionsOfTheBuiltInTemplate()
    {
        var studio = new StudioContext().Studio;
        var original = Assert.Single(studio.GetBuiltInTemplates());
        var versionTwo = await studio.CreateTemplateVersionAsync(original.TemplateId, 2, sourceVersion: 1);
        Assert.False(versionTwo.IsBuiltIn);

        var latest = await studio.GetRequiredTemplateAsync(original.TemplateId);

        Assert.Equal(2, latest.Version);
        Assert.Same(versionTwo, latest);
        Assert.Same(original, await studio.GetRequiredTemplateAsync(original.TemplateId, 1));
        Assert.Same(versionTwo, await studio.GetRequiredTemplateAsync(original.TemplateId, 2));
    }

    [Fact]
    public async Task Review_LatestLookupStillReturnsBuiltInWhenNoNewerStoredVersionExists()
    {
        var studio = new StudioContext().Studio;
        var original = Assert.Single(studio.GetBuiltInTemplates());
        Assert.Same(original, await studio.GetRequiredTemplateAsync(original.TemplateId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_DuplicateDocumentKindsCannotHideAnUnapprovedRevision(bool unapprovedFirst)
    {
        var approved = CreateGateDocument(DocumentTemplateKind.ProblemStatement,
            verdicts: new[] { CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve) },
            approval: CreateApproval(Hash('1')));
        var unapproved = CreateGateDocument(DocumentTemplateKind.ProblemStatement, contentHash: Hash('2'));
        var documents = unapprovedFirst ? new[] { unapproved, approved } : new[] { approved, unapproved };

        var result = new PreCoderGateValidator().Evaluate(new PreCoderGateRequest(
            new[] { DocumentTemplateKind.ProblemStatement }, new[] { "Reviewer" }, documents, "write"));

        Assert.False(result.IsAllowed);
        Assert.Contains(result.BlockingReasons, reason => reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }
}

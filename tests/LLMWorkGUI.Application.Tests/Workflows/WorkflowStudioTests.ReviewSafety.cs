using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowStudioTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedStudioPolicyDoesNotExposeAWritableCollection(bool reviewerRoles)
    {
        object policy = reviewerRoles
            ? WorkflowStudioDocumentRules.RequiredReviewerRoles
            : WorkflowStudioDocumentRules.RequiredDocumentKinds;

        // Do not actually mutate shared static policy under parallel tests. The standard IList
        // contract reports whether its setter can change the process-wide reviewer/document rules.
        if (policy is System.Collections.IList collection)
        {
            Assert.True(collection.IsReadOnly);
        }
    }

    [Fact]
    public async Task SwitchAccountContext_RefusalDoesNotExposeRawRequestedRouteContent()
    {
        const string requestContent = "private-route-secret\r\nforged-log-line";
        var result = await new StudioContext().Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(AccountContextKind.Agy, "account-1",
                RequestedRouteId: requestContent));

        Assert.DoesNotContain("private-route-secret", result.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-log-line", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal(requestContent, result.RequestedRouteId); // Explicitly requested data, never observation.
    }

    [Fact]
    public void BuiltInTemplateListingCannotReplaceTheServicesDefinition()
    {
        var studio = new StudioContext().Studio;
        var original = studio.GetBuiltInTemplates()[0];
        if (studio.GetBuiltInTemplates() is IList<WorkflowTemplateDefinition> list)
        {
            try { list[0] = original.Clone("replacement", "Replacement"); }
            catch (NotSupportedException) { }
        }

        Assert.Same(original, studio.GetBuiltInTemplates()[0]);
    }

    [Fact]
    public void GateRequestExposedRoleListCannotRewriteItsRequiredReviewerPolicy()
    {
        var request = new PreCoderGateRequest(new[] { DocumentTemplateKind.ProblemStatement },
            new[] { "Reviewer" }, Array.Empty<PreCoderGateDocument>(), "write");
        if (request.RequiredReviewerRoles is IList<string> roles)
        {
            try { roles[0] = "OptionalObserver"; }
            catch (NotSupportedException) { }
        }

        Assert.Equal("Reviewer", Assert.Single(request.RequiredReviewerRoles));
    }

    [Fact]
    public async Task SwitchAccountContext_UndefinedKindIsInvalidBeforeNativeProofIsConsidered()
    {
        var context = new StudioContext();
        var result = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest((AccountContextKind)42, "account-1"));

        Assert.Equal(WorkflowAccountContextSwitchRefusal.InvalidContext, result.Refusal);
        Assert.False(result.IsSwitched);
        Assert.Null(result.NativeSessionId);
    }

    [Fact]
    public void TemplateStore_SeedCannotReplaceAnImmutableVersion()
    {
        var original = WorkflowStudioService.CreateStandardTemplate().Clone("custom", "First");
        var replacement = original.Clone("custom", "Replacement");

        Assert.Throws<WorkflowValidationException>(() =>
            new InMemoryWorkflowTemplateStore(new[] { original, replacement }));
    }

    [Fact]
    public void PreCoderGate_StaleVerdictFromANonRequiredRoleDoesNotBlockRequiredApprovals()
    {
        var document = CreateGateDocument(DocumentTemplateKind.ProblemStatement,
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("OptionalObserver", Hash('2'), WorkflowReviewVerdict.Reject)
            }, approval: CreateApproval(Hash('1')));
        var result = new PreCoderGateValidator().Evaluate(new PreCoderGateRequest(
            new[] { document.Kind }, new[] { "Reviewer" }, new[] { document }, "write"));

        Assert.True(result.IsAllowed);
        Assert.False(result.IsHashMismatch);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PreCoderGate_OptionalDocumentCannotSupplyRequiredDocumentVisualEvidence(
        bool requiresUiArtifact, bool requiresVisualAcceptance)
    {
        var required = CreateGateDocument(DocumentTemplateKind.ProblemStatement,
            verdicts: new[] { CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve) },
            approval: CreateApproval(Hash('1')));
        var optional = CreateGateDocument(DocumentTemplateKind.Architecture,
            hasUiArtifact: true, hasVisualAcceptance: true);
        var result = new PreCoderGateValidator().Evaluate(new PreCoderGateRequest(
            new[] { required.Kind }, new[] { "Reviewer" }, new[] { required, optional }, "write",
            requiresUiArtifact: requiresUiArtifact,
            requiresUserVisualAcceptance: requiresVisualAcceptance));

        Assert.False(result.IsAllowed);
        Assert.Equal(requiresUiArtifact, result.HasMissingUiArtifact);
        Assert.Equal(requiresVisualAcceptance, result.HasMissingVisualAcceptance);
    }
}

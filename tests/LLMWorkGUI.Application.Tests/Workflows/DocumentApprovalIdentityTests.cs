using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class DocumentApprovalIdentityTests
{
    [Fact]
    public async Task DraftApproval_UsesHostIdentityInsteadOfCallerName()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IUserApprovalIdentity>(new FixedUserApprovalIdentity("local-reviewer"))
            .BuildServiceProvider();
        var documents = ActivatorUtilities.CreateInstance<DocumentTemplateService>(services);
        var draft = await documents.GenerateDraftAsync(DocumentTemplateKind.ProblemStatement, "Synthetic draft");

        var result = await documents.ApproveDraftAsync(draft.DraftId, Approval(draft.ContentHash));

        Assert.Equal("local-reviewer", result.UserApproval!.ApprovedBy);
    }

    [Fact]
    public async Task DraftApproval_WithoutHostIdentityIsRefused()
    {
        var documents = new DocumentTemplateService();
        var draft = await documents.GenerateDraftAsync(DocumentTemplateKind.ProblemStatement, "Synthetic draft");

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            documents.ApproveDraftAsync(draft.DraftId, Approval(draft.ContentHash)));
        Assert.Null(draft.UserApproval);
    }

    private static UserApprovalEvidence Approval(string hash) => new(
        "synthetic-approval", "forged-caller", "approval-stage", hash,
        UserApprovalDecision.Approved, "Synthetic explicit approval", DateTimeOffset.UtcNow);
}

using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class ReviewEvidenceStoreContractTests
{
    [Fact]
    public async Task ObservationUpdateCannotReplaceThePersistedReviewIdentity()
    {
        var store = new InMemoryReviewEvidenceRepository();
        var original = Evidence("run-original");
        await store.SaveAsync(original);
        var changed = new ReviewerExecutionEvidence("execution", "other-session", "other-run",
            "OtherReviewer", "other-stage", "other-route", "observed", "other-artifact", "other-hash",
            false, ExecutionState.Succeeded);

        await store.UpdateObservedAsync(changed);

        var stored = Assert.IsType<ReviewerExecutionEvidence>(await store.GetByExecutionIdAsync("execution"));
        Assert.Equal(original.SessionId, stored.SessionId);
        Assert.Equal(original.WorkflowRunId, stored.WorkflowRunId);
        Assert.Equal(original.StageId, stored.StageId);
        Assert.Equal(original.ReviewerRole, stored.ReviewerRole);
        Assert.Equal(original.RequestedRouteId, stored.RequestedRouteId);
        Assert.Equal(original.ReviewedArtifactId, stored.ReviewedArtifactId);
        Assert.Equal(original.ReviewedArtifactHash, stored.ReviewedArtifactHash);
        Assert.Equal(original.IsReadOnly, stored.IsReadOnly);
        Assert.Equal("observed", stored.ObservedRouteId);
    }

    [Fact]
    public async Task ASecondBindingCannotReplaceAnExistingExecutionId()
    {
        var store = new InMemoryReviewEvidenceRepository();
        var original = Evidence("run-original");
        await store.SaveAsync(original);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Evidence("other-run")));
        Assert.Same(original, await store.GetByExecutionIdAsync("execution"));
    }

    private static ReviewerExecutionEvidence Evidence(string runId) => new(
        "execution", "session", runId, "Reviewer", "stage", "route", null,
        "artifact", "hash", true, ExecutionState.Queued);
}

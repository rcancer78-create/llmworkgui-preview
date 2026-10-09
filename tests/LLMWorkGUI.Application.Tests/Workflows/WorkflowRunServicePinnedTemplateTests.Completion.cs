using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class WorkflowRunServicePinnedTemplateTests
{
    [Fact]
    public async Task CompleteRunAsync_CannotSkipAPinnedRunToSuccessfulCompletion()
    {
        await AssignAsync(1);
        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);

        await Assert.ThrowsAsync<WorkflowValidationException>(() => _service.CompleteRunAsync(run.Id, "skip"));

        var stored = (await _repository.GetByIdAsync(run.Id))!;
        Assert.False(stored.IsTerminal);
        Assert.Equal("node-a", stored.CurrentStageId);
        Assert.Empty(stored.Transitions);
    }

    [Fact]
    public async Task CompleteRunAsync_AcceptsAPinnedRunAfterItsFinalStageIsReached()
    {
        await AssignAsync(1);
        var run = await _service.StartRunAsync(ProjectId, PackageId, VersionId);
        await _service.AdvanceStageAsync(run.Id, "first");
        await _service.AdvanceStageAsync(run.Id, "second");

        var completed = await _service.CompleteRunAsync(run.Id, "Final outcome verified.");

        Assert.Equal(WorkflowRunState.Completed, completed.State);
        Assert.Equal("node-c", (await _repository.GetByIdAsync(run.Id))!.CurrentStageId);
    }
}

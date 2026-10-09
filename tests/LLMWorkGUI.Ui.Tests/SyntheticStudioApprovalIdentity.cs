using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Ui.Tests;

internal sealed class SyntheticStudioApprovalIdentity : IUserApprovalIdentity
{
    public string GetCurrentApproverIdentity() => "synthetic-studio-user";
}

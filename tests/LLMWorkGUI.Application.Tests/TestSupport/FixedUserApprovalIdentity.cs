using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Application.Tests.TestSupport;

/// <summary>
/// An approver identity the test chooses, standing in for the host's own. The run service stamps every
/// approval with whatever this resolves, so a test can prove that the name that arrived with the evidence
/// is replaced - and can resolve to nothing at all, which is the case the service has to fail closed on.
/// </summary>
internal sealed class FixedUserApprovalIdentity : IUserApprovalIdentity
{
    private readonly string? _name;

    public FixedUserApprovalIdentity(string? name) => _name = name;

    /// <summary>An identity that cannot be established, as on a host whose logon is unreadable.</summary>
    public static FixedUserApprovalIdentity Unavailable => new(null);

    public string? GetCurrentApproverIdentity() => _name;
}

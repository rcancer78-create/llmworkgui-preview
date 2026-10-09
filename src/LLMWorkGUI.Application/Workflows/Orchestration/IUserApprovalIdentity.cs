namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The identity a product user approval is recorded under, resolved by the host rather than supplied by the
/// caller of <see cref="IWorkflowRunService.RecordUserApprovalAsync"/>.
/// <para>
/// The abstraction exists so the run service can stamp an approval itself instead of believing the name
/// that arrived with the evidence. In this local WPF slice the name is the current Windows logon of the
/// window, which is an <em>unsigned</em> claim about who was sitting at the keyboard: it is not a provider
/// identity, not a backend account and not a cryptographic signature, and a value that is not available is
/// a refusal rather than a fallback name. Because the resolution happens inside the service, another caller
/// cannot name somebody else either - whatever <see cref="Domain.ValueObjects.UserApprovalEvidence"/>
/// carries is replaced with what this source resolves.
/// </para>
/// </summary>
public interface IUserApprovalIdentity
{
    /// <summary>
    /// The current local Windows logon name, or <c>null</c>/whitespace when it cannot be established. The
    /// service treats both as "no identity" and refuses to record the approval.
    /// </summary>
    string? GetCurrentApproverIdentity();
}

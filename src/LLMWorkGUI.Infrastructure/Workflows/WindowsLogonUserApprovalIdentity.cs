using System.Security;
using System.Security.Principal;
using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// The production approver identity of this slice: the Windows logon the application is running as, read
/// from the process token through <see cref="WindowsIdentity"/>.
/// <para>
/// It is deliberately a plain name. The token says who is logged on to the operating system; it says
/// nothing about a provider account, a backend or a signature, and nothing here upgrades it into one. A
/// host that is not Windows, an unreadable token and a shut-down security subsystem all answer "no
/// identity" instead of a placeholder, which is what lets the run service refuse rather than invent a name.
/// </para>
/// </summary>
public sealed class WindowsLogonUserApprovalIdentity : IUserApprovalIdentity
{
    /// <inheritdoc />
    public string? GetCurrentApproverIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            return identity?.Name;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
            or SecurityException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            // The point of the abstraction: an unavailable identity is an answer the caller can fail closed
            // on, and the fault text itself is never a name.
            return null;
        }
    }
}

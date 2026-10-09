namespace LLMWorkGUI.Application.Workflows.Declarative;

/// <summary>
/// A channel that can name why it declined a route.
/// <para>
/// <see cref="IWorkflowNodeChannel.SupportsRoute"/> is a boolean, and a boolean cannot say anything. A
/// catalog that finds no matching channel therefore refuses a reviewer assignment with a message about the
/// lookup having failed, which is true and useless: the operator is left to guess whether a channel is
/// missing, mis-registered, or provably unable to serve that route, and the second and third of those are
/// exactly the answers that tell them what to build next.
/// </para>
/// <para>
/// A channel that declines for a knowable reason implements this, and the catalog appends that reason to
/// the refusal. The reason must name what is missing, never a secret, never artifact content and never a
/// local path, and it must not imply that a turn happened: a channel that declined did not dispatch, and
/// nothing it says may be read as an observation.
/// </para>
/// </summary>
public interface IWorkflowChannelRouteRefusal
{
    /// <summary>
    /// One line naming why this channel does not support <paramref name="routeId"/>. It is operator-facing
    /// text: it states the missing fact, and it states that nothing was dispatched.
    /// </summary>
    string DescribeRouteRefusal(string routeId);
}

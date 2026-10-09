namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowChannelCatalog : IWorkflowChannelCatalog
{
    private readonly IReadOnlyList<IWorkflowNodeChannel> _channels;

    public WorkflowChannelCatalog(IEnumerable<IWorkflowNodeChannel>? channels = null)
    {
        _channels = channels?.ToArray() ?? Array.Empty<IWorkflowNodeChannel>();
    }

    public IReadOnlyList<IWorkflowNodeChannel> Channels => _channels;

    public IWorkflowNodeChannel ResolveChannel(
        string routeId,
        IReadOnlyList<string> requiredCapabilities)
    {
        var guardedRouteId = ApplicationGuard.NotBlank(routeId, nameof(routeId));
        ArgumentNullException.ThrowIfNull(requiredCapabilities);

        // Every channel that declined is asked why, so the refusal can name the missing fact instead of
        // only reporting that the lookup found nothing. A channel that cannot say why is simply absent
        // from the message, which is why this list is additive and never the whole answer.
        var declined = new List<string>();

        foreach (var channel in _channels)
        {
            if (!channel.SupportsRoute(guardedRouteId))
            {
                if (channel is IWorkflowChannelRouteRefusal explaining)
                {
                    declined.Add(explaining.DescribeRouteRefusal(guardedRouteId));
                }

                continue;
            }

            if (requiredCapabilities.All(capability =>
                channel.Capabilities.Contains(capability, StringComparer.Ordinal)))
            {
                return channel;
            }
        }

        var requestedCapabilities = requiredCapabilities.Count == 0
            ? "<none>"
            : string.Join(", ", requiredCapabilities);

        var reasons = declined.Count == 0
            ? string.Empty
            : $" Declined: {string.Join(" ", declined)}";

        throw new WorkflowValidationException(
            $"No channel with proven capabilities [{requestedCapabilities}] supports route "
            + $"'{guardedRouteId}'. Automatic execution is refused; a catalog entry alone does not "
            + $"authorize a turn (ТЗ §6.15).{reasons}");
    }
}

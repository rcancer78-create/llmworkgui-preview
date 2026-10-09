using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public sealed class ActivityTimeline
{
    public static ActivityTimeline Empty { get; } = new(Array.Empty<ActivityTimelineItem>());

    public ActivityTimeline(IReadOnlyList<ActivityTimelineItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items = items.ToArray();
        CurrentItem = FindCurrentItem(Items);
        ActiveRole = CurrentItem?.Role ?? WorkflowRole.Unknown;
    }

    public IReadOnlyList<ActivityTimelineItem> Items { get; }

    public ActivityTimelineItem? CurrentItem { get; }

    public WorkflowRole ActiveRole { get; }

    public bool IsEmpty => Items.Count == 0;

    public bool HasSyntheticItems => Items.Any(item => item.IsSynthetic);

    private static ActivityTimelineItem? FindCurrentItem(IReadOnlyList<ActivityTimelineItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index].IsActive)
            {
                return items[index];
            }
        }

        return null;
    }
}

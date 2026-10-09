using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ModelItemViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public int? ContextWindow { get; }
    public ModelCapabilityFlags Capabilities { get; }
    public IReadOnlyList<string> SupportedReasoningEfforts { get; }

    public bool SupportsVision => (Capabilities & ModelCapabilityFlags.Vision) != 0;
    public bool SupportsToolCalling => (Capabilities & ModelCapabilityFlags.ToolCalling) != 0;
    public bool SupportsReasoning => (Capabilities & ModelCapabilityFlags.ReasoningVariants) != 0;

    public string CapabilitiesSummary
    {
        get
        {
            var list = new List<string>();
            if ((Capabilities & ModelCapabilityFlags.Chat) != 0) list.Add("Chat");
            if (SupportsVision) list.Add("Vision");
            if (SupportsToolCalling) list.Add("Tools");
            if (SupportsReasoning) list.Add("Reasoning");
            return list.Count == 0 ? "Возможности не подтверждены" : string.Join(" | ", list);
        }
    }

    public ModelItemViewModel(DiscoveredModelDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        Id = details.Id;
        Name = details.Name;
        Description = details.Description;
        ContextWindow = details.ContextWindow;
        Capabilities = details.Capabilities;
        SupportedReasoningEfforts = details.SupportedReasoningEfforts;
    }
}

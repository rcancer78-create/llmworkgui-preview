using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One semantic role mapping row of the adaptation candidate summary table.</summary>
public sealed class SemanticRoleMappingViewModel : ObservableObject
{
    public SemanticRoleMappingViewModel(SemanticRoleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        Mapping = mapping;
    }

    public SemanticRoleMapping Mapping { get; }

    public string Role => Mapping.Role.ToString();

    public string OriginalRoute => Mapping.OriginalRoute;

    public string TargetRoute => Mapping.TargetRoute;

    public string TargetModelId => Mapping.TargetModelId;

    public string Rationale => Mapping.Rationale;

    public bool IsSemanticChange => Mapping.IsSemanticChange;

    public string SemanticChangeDisplay =>
        IsSemanticChange ? "Semantic change" : "No semantic change";

    public string BlockerKindDisplay => Mapping.BlockerKind?.ToString() ?? "None";
}

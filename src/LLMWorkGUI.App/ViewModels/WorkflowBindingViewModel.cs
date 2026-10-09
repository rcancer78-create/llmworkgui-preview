using System.Globalization;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One project-to-package binding row of the workflow library.</summary>
public sealed class WorkflowBindingViewModel : ObservableObject
{
    private readonly string? _projectDisplayName;

    public WorkflowBindingViewModel(WorkflowBinding binding, string? projectDisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        Binding = binding;
        _projectDisplayName = projectDisplayName;
    }

    public WorkflowBinding Binding { get; }

    public string BindingId => Binding.Id;

    public string ProjectDisplay =>
        string.IsNullOrWhiteSpace(_projectDisplayName) ? Binding.ProjectId : _projectDisplayName!;

    public string ProjectIdDisplay => Binding.ProjectId;

    public string WorkflowPackageId => Binding.WorkflowPackageId;

    public string ActiveVersionIdDisplay => Binding.ActiveVersionId;

    public string RoutePolicyDisplay =>
        string.IsNullOrWhiteSpace(Binding.RoutePolicyId) ? "No route policy" : Binding.RoutePolicyId!;

    public string CreatedAtDisplay =>
        Binding.CreatedAtUtc.ToString("u", CultureInfo.InvariantCulture);

    public string UpdatedAtDisplay =>
        Binding.UpdatedAtUtc.ToString("u", CultureInfo.InvariantCulture);
}

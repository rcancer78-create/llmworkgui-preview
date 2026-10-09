using System.Globalization;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One immutable workflow package card in the library.</summary>
public sealed class WorkflowPackageItemViewModel : ObservableObject
{
    public WorkflowPackageItemViewModel(WorkflowPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        Package = package;
    }

    public WorkflowPackage Package { get; }

    public string Id => Package.Id;

    public string Name => Package.Name;

    public string DescriptionDisplay =>
        string.IsNullOrWhiteSpace(Package.Description) ? "No description" : Package.Description!;

    public string TagsDisplay =>
        Package.Tags.Count == 0 ? "No tags" : string.Join(", ", Package.Tags);

    public string SourceTypeDisplay => Package.SourceType.ToString();

    public string OriginalHashDisplay => Package.OriginalHash;

    public string CreatedAtDisplay =>
        Package.CreatedAtUtc.ToString("u", CultureInfo.InvariantCulture);

    public string UpdatedAtDisplay =>
        Package.UpdatedAtUtc.ToString("u", CultureInfo.InvariantCulture);
}

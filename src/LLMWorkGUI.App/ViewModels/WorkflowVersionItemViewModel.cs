using System.Globalization;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// One immutable workflow version. Only <see cref="IsActiveInCurrentProject"/> is mutable UI state: it
/// reflects the active-version pointer of the binding for the project selected right now, and it never
/// rewrites the version itself. Candidate/archived lifecycle states are not part of this slice.
/// </summary>
public sealed class WorkflowVersionItemViewModel : ObservableObject
{
    private bool _isActiveInCurrentProject;

    public WorkflowVersionItemViewModel(WorkflowVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        Version = version;
    }

    public WorkflowVersion Version { get; }

    public string Id => Version.Id;

    public int VersionNumber => Version.VersionNumber;

    public string VersionDisplay => string.Create(CultureInfo.InvariantCulture, $"v{Version.VersionNumber}");

    public string BlobIdDisplay => Version.BlobId;

    public string CreatedAtDisplay =>
        Version.CreatedAtUtc.ToString("u", CultureInfo.InvariantCulture);

    public string ActivatedAtDisplay => Version.ActivatedAtUtc is null
        ? "Never activated"
        : Version.ActivatedAtUtc.Value.ToString("u", CultureInfo.InvariantCulture);

    /// <summary>
    /// True only while this version is the active version of the binding for the currently selected
    /// project. Without a selected project or binding it is false rather than guessed.
    /// </summary>
    public bool IsActiveInCurrentProject
    {
        get => _isActiveInCurrentProject;
        set
        {
            if (SetProperty(ref _isActiveInCurrentProject, value))
            {
                OnPropertyChanged(nameof(ActiveStateDisplay));
            }
        }
    }

    public string ActiveStateDisplay =>
        IsActiveInCurrentProject ? "Active in this project" : "Not active in this project";
}

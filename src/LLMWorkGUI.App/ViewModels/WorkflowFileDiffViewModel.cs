using System.Globalization;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One file row of the candidate package diff with its unified diff text.</summary>
public sealed class WorkflowFileDiffViewModel : ObservableObject
{
    public WorkflowFileDiffViewModel(WorkflowFileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        Diff = diff;
    }

    public WorkflowFileDiff Diff { get; }

    public string RelativePath => Diff.RelativePath;

    public WorkflowFileDiffKind Kind => Diff.Kind;

    public string KindDisplay => Diff.Kind switch
    {
        WorkflowFileDiffKind.Added => "Добавлен",
        WorkflowFileDiffKind.Modified => "Изменён",
        WorkflowFileDiffKind.Deleted => "Удалён",
        _ => "Без изменений"
    };

    public int LinesAdded => Diff.LinesAdded;

    public int LinesDeleted => Diff.LinesDeleted;

    /// <summary>Compact "+N -N" badge shown next to the file status.</summary>
    public string ChangeBadge => string.Create(
        CultureInfo.InvariantCulture,
        $"+{LinesAdded} -{LinesDeleted}");

    public string UnifiedDiffText => Diff.UnifiedDiffText;

    public bool IsBinary => Diff.IsBinary;

    public string StatusDisplay => IsBinary
        ? string.Concat(KindDisplay, " (бинарный) ", ChangeBadge)
        : string.Concat(KindDisplay, " ", ChangeBadge);
}

namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowPackageDiff
{
    public WorkflowPackageDiff(IReadOnlyList<WorkflowFileDiff> fileDiffs)
    {
        ArgumentNullException.ThrowIfNull(fileDiffs);

        FileDiffs = fileDiffs.ToArray();
        TotalFilesAdded = FileDiffs.Count(diff => diff.Kind == WorkflowFileDiffKind.Added);
        TotalFilesModified = FileDiffs.Count(diff => diff.Kind == WorkflowFileDiffKind.Modified);
        TotalFilesDeleted = FileDiffs.Count(diff => diff.Kind == WorkflowFileDiffKind.Deleted);
        TotalFilesUnchanged = FileDiffs.Count(diff => diff.Kind == WorkflowFileDiffKind.Unchanged);
        TotalLinesAdded = FileDiffs.Sum(diff => diff.LinesAdded);
        TotalLinesDeleted = FileDiffs.Sum(diff => diff.LinesDeleted);
    }

    public IReadOnlyList<WorkflowFileDiff> FileDiffs { get; }

    public int TotalFilesAdded { get; }

    public int TotalFilesModified { get; }

    public int TotalFilesDeleted { get; }

    public int TotalFilesUnchanged { get; }

    public int TotalLinesAdded { get; }

    public int TotalLinesDeleted { get; }
}

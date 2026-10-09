namespace LLMWorkGUI.Application.Workflows;

public sealed record WorkflowFileDiff
{
    public WorkflowFileDiff(
        string relativePath,
        WorkflowFileDiffKind kind,
        int linesAdded,
        int linesDeleted,
        string unifiedDiffText,
        bool isBinary)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workflow file diff kind.");
        }

        if (linesAdded < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(linesAdded), "Added line count must not be negative.");
        }

        if (linesDeleted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(linesDeleted), "Deleted line count must not be negative.");
        }

        RelativePath = ApplicationGuard.NotBlank(relativePath, nameof(relativePath));
        Kind = kind;
        LinesAdded = linesAdded;
        LinesDeleted = linesDeleted;
        UnifiedDiffText = unifiedDiffText ?? throw new ArgumentNullException(nameof(unifiedDiffText));
        IsBinary = isBinary;
    }

    public string RelativePath { get; }

    public WorkflowFileDiffKind Kind { get; }

    public int LinesAdded { get; }

    public int LinesDeleted { get; }

    /// <summary>Standard unified diff text; empty for unchanged files.</summary>
    public string UnifiedDiffText { get; }

    public bool IsBinary { get; }
}

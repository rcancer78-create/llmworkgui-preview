namespace LLMWorkGUI.Application.Retention;

public sealed record RetentionCategoryResult(
    string Category,
    int DatabaseRowsDeleted,
    int FilesDeleted,
    int SkippedItems);

public sealed record RetentionRunReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<RetentionCategoryResult> Categories)
{
    public int TotalDatabaseRowsDeleted => Categories.Sum(category => category.DatabaseRowsDeleted);

    public int TotalFilesDeleted => Categories.Sum(category => category.FilesDeleted);

    public int TotalSkippedItems => Categories.Sum(category => category.SkippedItems);

    public RetentionCategoryResult? Find(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return Categories.FirstOrDefault(
            item => string.Equals(item.Category, category, StringComparison.Ordinal));
    }
}

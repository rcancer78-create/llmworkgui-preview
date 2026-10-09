namespace LLMWorkGUI.Application.Workflows;

public static class WorkflowImportLimits
{
    public const int MaxArchiveSizeBytes = 100 * 1024 * 1024;

    public const int MaxUncompressedTotalBytes = 500 * 1024 * 1024;

    public const int MaxFileCount = 10_000;

    public const int MaxSingleFileBytes = 20 * 1024 * 1024;

    public const double MaxCompressionRatio = 100.0;
}

namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Stable category labels of the diagnostic bundle preview. They are part of the preview contract so
/// the UI can group files without parsing relative paths (ТЗ §9.3).
/// </summary>
public static class DiagnosticBundleCategories
{
    public const string Environment = "environment";

    public const string Configuration = "configuration";

    public const string Database = "database";

    public const string Logs = "logs";

    public const string RunLogs = "run-logs";

    public const string Audit = "audit";

    public const string Manifest = "manifest";
}

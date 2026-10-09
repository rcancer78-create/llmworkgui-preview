namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Selection of content for one diagnostic bundle. Every collected file still passes the redaction
/// pipeline and the secret scan before it can be exported (ТЗ §9.3).
/// </summary>
public sealed record DiagnosticBundleRequest
{
    public const int DefaultMaxLogFiles = 200;

    public const int DefaultMaxLogBytesPerFile = 512 * 1024;

    /// <summary>Default request: application logs, run logs, health audit and database schema.</summary>
    public static DiagnosticBundleRequest Default { get; } = new();

    /// <summary>
    /// Explicit destination of the generated <c>.zip</c>. When null the bundle is written into the
    /// managed diagnostics directory.
    /// </summary>
    public string? OutputPath { get; init; }

    public bool IncludeApplicationLogs { get; init; } = true;

    public bool IncludeRunLogs { get; init; } = true;

    public bool IncludeHealthAudit { get; init; } = true;

    public bool IncludeDatabaseSchema { get; init; } = true;

    public int MaxLogFiles { get; init; } = DefaultMaxLogFiles;

    public int MaxLogBytesPerFile { get; init; } = DefaultMaxLogBytesPerFile;

    public void Validate()
    {
        if (MaxLogFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLogFiles), "At least one log file must be allowed.");
        }

        if (MaxLogBytesPerFile <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxLogBytesPerFile),
                "The per-file log budget must be positive.");
        }
    }
}

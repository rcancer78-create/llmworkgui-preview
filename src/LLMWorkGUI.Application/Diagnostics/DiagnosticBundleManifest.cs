using System.Text.Json;

namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Manifest embedded into every generated bundle. It records what was included and confirms that the
/// redaction pipeline and secret scan ran before the archive was written (ТЗ §9.3).
/// </summary>
public sealed record DiagnosticBundleManifest
{
    public const string CurrentSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public required string PreviewId { get; init; }

    public required string BundleSchemaVersion { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required string ApplicationVersion { get; init; }

    public required string OperatingSystem { get; init; }

    public required string MachineName { get; init; }

    public required string Runtime { get; init; }

    public required int DatabaseSchemaVersion { get; init; }

    public required IReadOnlyList<string> IncludedFiles { get; init; }

    public required int RedactedFileCount { get; init; }

    /// <summary>Count of secret URN references; references are metadata, never secret material.</summary>
    public required int ReferenceCount { get; init; }

    /// <summary>Always false for an exportable bundle; a blocked preview is never archived.</summary>
    public required bool ContainsSensitiveData { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}

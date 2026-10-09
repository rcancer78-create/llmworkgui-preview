using System.IO;
using System.Text.Json;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Default <see cref="ILayoutPersistenceService"/>: a single local JSON file, using
/// <c>System.Text.Json</c> only. A missing, unreadable or corrupt file resolves to defaults instead of
/// failing window startup; a failed save is swallowed for the same reason (ROADMAP Phase 11).
/// <para>
/// The file lives in the application's own data root. <see cref="ResolveFilePath(string?)"/> keeps the
/// shipped default <c>%LOCALAPPDATA%\LLMWorkGUI\shell-layout.json</c> for an ordinary production start,
/// and puts the same file inside whatever root the host was configured with otherwise - so a host built
/// over a temporary or isolated root never reads or writes the real user's shell layout.
/// </para>
/// </summary>
public sealed class LayoutPersistenceService : ILayoutPersistenceService
{
    public const string DefaultDirectoryName = "LLMWorkGUI";
    public const string DefaultFileName = "shell-layout.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LayoutPersistenceService(string? filePath = null)
    {
        FilePath = filePath ?? ResolveFilePath(appDataDirectory: null);
    }

    /// <summary>
    /// The layout file for a configured application-data root, and the shipped
    /// <c>%LOCALAPPDATA%\LLMWorkGUI\shell-layout.json</c> when no root is configured or it is blank.
    /// </summary>
    public static string ResolveFilePath(string? appDataDirectory) =>
        string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultDirectoryName,
                DefaultFileName)
            : Path.GetFullPath(Path.Combine(appDataDirectory, DefaultFileName));

    /// <summary>Absolute path of the JSON file this instance reads and writes.</summary>
    public string FilePath { get; }

    public ShellLayoutState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new ShellLayoutState();
            }

            var json = File.ReadAllText(FilePath);

            return JsonSerializer.Deserialize<ShellLayoutState>(json, SerializerOptions)
                ?? new ShellLayoutState();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new ShellLayoutState();
        }
    }

    public void Save(ShellLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, SerializerOptions));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Layout persistence is a comfort feature: a failed write must not break the shell.
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException;
}

namespace LLMWorkGUI.Infrastructure.Storage;

public static class AppDataPaths
{
    public const string ApplicationDirectoryName = "LLMWorkGUI";
    public const string SecretsDirectoryName = "secrets";
    public const string BlobsDirectoryName = "blobs";
    public const string LogsDirectoryName = "logs";
    public const string DiagnosticBundlesDirectoryName = "diagnostics";
    public const string RunsDirectoryName = "runs";

    private const int MaxExecutionIdLength = 64;

    public static string DefaultRootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationDirectoryName);

    public static string GetSecretsDirectory(string? rootDirectory = null)
    {
        return Path.Combine(rootDirectory ?? DefaultRootDirectory, SecretsDirectoryName);
    }

    public static string GetBlobsRootDirectory(string? rootDirectory = null)
    {
        return Path.Combine(rootDirectory ?? DefaultRootDirectory, BlobsDirectoryName);
    }

    public static string GetLogsDirectory(string? rootDirectory = null)
    {
        return Path.Combine(rootDirectory ?? DefaultRootDirectory, LogsDirectoryName);
    }

    public static string GetDiagnosticBundlesDirectory(string? rootDirectory = null)
    {
        return Path.Combine(rootDirectory ?? DefaultRootDirectory, DiagnosticBundlesDirectoryName);
    }

    public static string GetRunsRootDirectory(string? rootDirectory = null)
    {
        return Path.Combine(rootDirectory ?? DefaultRootDirectory, RunsDirectoryName);
    }

    public static string GetRunDirectory(string? rootDirectory, string executionId)
    {
        EnsureValidExecutionId(executionId);

        return Path.Combine(GetRunsRootDirectory(rootDirectory), executionId);
    }

    public static string GetDatabasePath(
        string? rootDirectory = null,
        string databaseFileName = "llmworkgui.db")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFileName);

        return Path.Combine(rootDirectory ?? DefaultRootDirectory, databaseFileName);
    }

    private static void EnsureValidExecutionId(string executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new ArgumentException("Execution id must not be null, empty, or whitespace.", nameof(executionId));
        }

        if (executionId.Length > MaxExecutionIdLength)
        {
            throw new ArgumentException(
                $"Execution id must not exceed {MaxExecutionIdLength} characters.",
                nameof(executionId));
        }

        if (executionId is "." or "..")
        {
            throw new ArgumentException("Execution id must not be a relative path segment.", nameof(executionId));
        }

        foreach (var character in executionId)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                throw new ArgumentException(
                    $"Execution id contains an unsupported character '{character}'.",
                    nameof(executionId));
            }
        }
    }
}

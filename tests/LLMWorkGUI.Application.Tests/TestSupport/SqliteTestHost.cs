using System.Globalization;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Application.Tests.TestSupport;

/// <summary>
/// Temporary SQLite database with the production migrations and the minimum seed rows required by
/// repository foreign keys. Every instance owns an isolated temp directory.
/// </summary>
internal sealed class SqliteTestHost : IDisposable
{
    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public SqliteTestHost()
    {
        _root = Path.Combine(Path.GetTempPath(), "llmworkgui-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        Factory = new SqliteConnectionFactory(Path.Combine(_root, "llmworkgui.db"));
    }

    public SqliteConnectionFactory Factory { get; }

    public string Root => _root;

    public string DatabasePath => Path.Combine(_root, "llmworkgui.db");

    public string GetWorkspacePath(string name = "workspace")
    {
        return Path.Combine(_root, name);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseMigrator(Factory).MigrateAsync();
    }

    public async Task SeedRouteChainAsync(
        string projectId = "project-1",
        string? projectRootPath = null,
        string providerProfileId = "provider-1",
        string accountId = "account-1",
        string modelId = "model-1",
        string routeId = "route-1")
    {
        var timestamp = FormatTimestamp(SeedTimestamp);
        projectRootPath ??= GetWorkspacePath();

        await ExecuteAsync(
            """
            INSERT INTO ProviderProfiles
                (Id, DisplayName, Backend, MaxDataClass, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Test provider', 'OpenCode', 'PrivateSource', 1, $timestamp, $timestamp);
            """,
            ("$id", providerProfileId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO Accounts
                (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $providerProfileId, 'Test account', 'Valid', 'Healthy', $timestamp, $timestamp);
            """,
            ("$id", accountId),
            ("$providerProfileId", providerProfileId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO Models
                (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState,
                 Provenance, IsEnabled, Health, DiscoveredAtUtc)
            VALUES ($id, 'OpenCode', $providerProfileId, $id, 'Test model', 'Supported',
                    'ProviderReported', 1, 'Healthy', $timestamp);
            """,
            ("$id", modelId),
            ("$providerProfileId", providerProfileId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO Routes
                (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, IsEnabled,
                 Health, ManualPriority, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'OpenCode', $providerProfileId, $accountId, $modelId, 'PrivateSource',
                    1, 'Healthy', 0, $timestamp, $timestamp);
            """,
            ("$id", routeId),
            ("$providerProfileId", providerProfileId),
            ("$accountId", accountId),
            ("$modelId", modelId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO Projects
                (Id, DisplayName, RootPath, IsDirty, HasRequiredInstructions, DataClassification,
                 CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Test project', $rootPath, 0, 1, 'PrivateSource', $timestamp, $timestamp);
            """,
            ("$id", projectId),
            ("$rootPath", ProjectLock.CanonicalizeRoot(projectRootPath)),
            ("$timestamp", timestamp));
    }

    public async Task SeedSessionAsync(
        string sessionId = "session-1",
        string projectId = "project-1",
        string state = "Active",
        string? workspaceRootPath = null,
        string providerProfileId = "provider-1",
        string accountId = "account-1",
        string modelId = "model-1",
        string? activeExecutionId = null,
        string reconciliationOutcome = "None")
    {
        var timestamp = FormatTimestamp(SeedTimestamp);
        workspaceRootPath ??= GetWorkspacePath();

        await ExecuteAsync(
            """
            INSERT INTO Sessions
                (Id, ProjectId, Backend, ProviderProfileId, AccountId, ModelId, WorkspaceRootPath,
                 NativeSessionId, State, ReconciliationOutcome, CloseReason, ActiveExecutionId,
                 CreatedAtUtc, LastEventAtUtc)
            VALUES ($id, $projectId, 'OpenCode', $providerProfileId, $accountId, $modelId,
                    $workspaceRootPath, NULL, $state, $reconciliationOutcome, 'None', $activeExecutionId,
                    $timestamp, $timestamp);
            """,
            ("$id", sessionId),
            ("$projectId", projectId),
            ("$providerProfileId", providerProfileId),
            ("$accountId", accountId),
            ("$modelId", modelId),
            ("$workspaceRootPath", workspaceRootPath),
            ("$state", state),
            ("$reconciliationOutcome", reconciliationOutcome),
            ("$activeExecutionId", activeExecutionId),
            ("$timestamp", timestamp));
    }

    public async Task SeedExecutionAsync(
        string executionId,
        string sessionId = "session-1",
        string state = "Running",
        string routeId = "route-1",
        string clientRequestId = "client-request-1",
        string failureReason = "None",
        string? terminationReason = null,
        DateTimeOffset? endedAt = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO Executions
                (Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId,
                 TerminationReason, CreatedAtUtc, EndedAtUtc)
            VALUES ($id, $sessionId, $clientRequestId, $state, $failureReason, $routeId,
                    $terminationReason, $timestamp, $endedAtUtc);
            """,
            ("$id", executionId),
            ("$sessionId", sessionId),
            ("$clientRequestId", clientRequestId),
            ("$state", state),
            ("$failureReason", failureReason),
            ("$routeId", routeId),
            ("$terminationReason", terminationReason),
            ("$timestamp", FormatTimestamp(SeedTimestamp)),
            ("$endedAtUtc", endedAt is null ? null : FormatTimestamp(endedAt.Value)));
    }

    public async Task SeedWorkflowAsync(
        string packageId = "package-1",
        string versionId = "version-1",
        string runId = "run-1",
        string projectId = "project-1",
        string state = "Running",
        DateTimeOffset? startedAt = null)
    {
        var timestamp = FormatTimestamp(startedAt ?? SeedTimestamp);
        var blobId = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(packageId))).ToLowerInvariant();
        var isTerminal = state is "Completed" or "Failed" or "Cancelled";
        var terminalOutcome = isTerminal ? state : "None";
        var endedAtUtc = isTerminal ? timestamp : null;

        await ExecuteAsync(
            """
            INSERT INTO WorkflowPackages
                (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Test package', 'Imported', $hash, $blobId, $timestamp, $timestamp);
            """,
            ("$id", packageId),
            ("$hash", blobId),
            ("$blobId", blobId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO WorkflowVersions
                (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($id, $packageId, 1, $blobId, $hash, 'Imported', $timestamp);
            """,
            ("$id", versionId),
            ("$packageId", packageId),
            ("$blobId", blobId),
            ("$hash", blobId),
            ("$timestamp", timestamp));

        await ExecuteAsync(
            """
            INSERT INTO WorkflowRuns
                (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, SessionId, State,
                 StartedAtUtc, EndedAtUtc, TerminalOutcome, EvidenceRedactedJson)
            VALUES ($id, $projectId, $packageId, $versionId, NULL, $state,
                    $timestamp, $endedAtUtc, $terminalOutcome, $evidence);
            """,
            ("$id", runId),
            ("$projectId", projectId),
            ("$packageId", packageId),
            ("$versionId", versionId),
            ("$state", state),
            ("$timestamp", timestamp),
            ("$endedAtUtc", endedAtUtc),
            ("$terminalOutcome", terminalOutcome),
            ("$evidence", """{"currentStageId":"implementation","currentRole":"Coder"}"""));
    }

    public async Task InsertProjectLockAsync(
        string lockId,
        string canonicalRootPath,
        string executionId,
        string projectId = "project-1",
        string applicationInstanceId = "previous-instance")
    {
        var normalizedRootPath = ProjectLock.CanonicalizeRoot(canonicalRootPath);

        if (OperatingSystem.IsWindows())
        {
            normalizedRootPath = normalizedRootPath.ToUpperInvariant();
        }

        await ExecuteAsync(
            """
            INSERT INTO ProjectLocks
                (Id, ProjectId, CanonicalRootPath, ExecutionId, ApplicationInstanceId,
                 ProcessGeneration, AcquiredAtUtc, ReleasedAtUtc, ReleaseReason)
            VALUES ($id, $projectId, $canonicalRootPath, $executionId, $applicationInstanceId,
                    1, $acquiredAtUtc, NULL, NULL);
            """,
            ("$id", lockId),
            ("$projectId", projectId),
            ("$canonicalRootPath", normalizedRootPath),
            ("$executionId", executionId),
            ("$applicationInstanceId", applicationInstanceId),
            ("$acquiredAtUtc", FormatTimestamp(SeedTimestamp)));
    }

    public async Task InsertHealthEventAsync(
        string id,
        string scopeType,
        string scopeId,
        string newState = "Degraded",
        DateTimeOffset? occurredAt = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO HealthEvents (Id, ScopeType, ScopeId, NewState, OccurredAtUtc)
            VALUES ($id, $scopeType, $scopeId, $state, $occurredAtUtc);
            """,
            ("$id", id),
            ("$scopeType", scopeType),
            ("$scopeId", scopeId),
            ("$state", newState),
            ("$occurredAtUtc", FormatTimestamp(occurredAt ?? SeedTimestamp)));
    }

    public async Task<long> CountAsync(string tableName, string? whereClause = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var sql = $"SELECT COUNT(*) FROM {tableName}";

        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += $" WHERE {whereClause}";
        }

        await using var connection = await Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql + ";";

        var value = await command.ExecuteScalarAsync();

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

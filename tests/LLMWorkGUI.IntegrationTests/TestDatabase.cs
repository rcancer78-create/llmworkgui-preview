using System.Globalization;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.IntegrationTests;

internal sealed class TestDatabase : IDisposable
{
    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDirectory _directory = new();

    public TestDatabase()
    {
        Factory = new SqliteConnectionFactory(_directory.GetPath("llmworkgui.db"));
    }

    public SqliteConnectionFactory Factory { get; }

    public string Root => _directory.Root;

    public string GetWorkspacePath(string name = "workspace")
    {
        return Path.Combine(Root, name);
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
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
        string? nativeSessionId = null,
        string reconciliationOutcome = "None",
        string closeReason = "None",
        string? activeExecutionId = null)
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
                    $workspaceRootPath, $nativeSessionId, $state, $reconciliationOutcome, $closeReason,
                    $activeExecutionId, $timestamp, $timestamp);
            """,
            ("$id", sessionId),
            ("$projectId", projectId),
            ("$providerProfileId", providerProfileId),
            ("$accountId", accountId),
            ("$modelId", modelId),
            ("$workspaceRootPath", workspaceRootPath),
            ("$nativeSessionId", nativeSessionId),
            ("$state", state),
            ("$reconciliationOutcome", reconciliationOutcome),
            ("$closeReason", closeReason),
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
        string? processState = null,
        DateTimeOffset? endedAt = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO Executions
                (Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId,
                 ProcessState, CreatedAtUtc, EndedAtUtc)
            VALUES ($id, $sessionId, $clientRequestId, $state, $failureReason, $routeId,
                    $processState, $timestamp, $endedAtUtc);
            """,
            ("$id", executionId),
            ("$sessionId", sessionId),
            ("$clientRequestId", clientRequestId),
            ("$state", state),
            ("$failureReason", failureReason),
            ("$routeId", routeId),
            ("$processState", processState),
            ("$timestamp", FormatTimestamp(SeedTimestamp)),
            ("$endedAtUtc", endedAt is null ? null : FormatTimestamp(endedAt.Value)));
    }

    public async Task UpdateExecutionStateAsync(string executionId, string state)
    {
        await ExecuteAsync(
            "UPDATE Executions SET State = $state WHERE Id = $id;",
            ("$id", executionId),
            ("$state", state));
    }

    public async Task UpdateSessionStateAsync(string sessionId, string state)
    {
        await ExecuteAsync(
            "UPDATE Sessions SET State = $state WHERE Id = $id;",
            ("$id", sessionId),
            ("$state", state));
    }

    public async Task InsertExecutionEventAsync(
        string id,
        string executionId,
        long sequence,
        DateTimeOffset occurredAt,
        string eventKind = "Output",
        string? normalizedPayload = null,
        string? rawPayload = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO ExecutionEvents
                (Id, ExecutionId, Sequence, EventKind, NormalizedRedactedPayloadJson,
                 RawRedactedPayloadText, DataClassification, OccurredAtUtc)
            VALUES ($id, $executionId, $sequence, $eventKind, $normalizedPayload,
                    $rawPayload, NULL, $occurredAtUtc);
            """,
            ("$id", id),
            ("$executionId", executionId),
            ("$sequence", sequence),
            ("$eventKind", eventKind),
            ("$normalizedPayload", normalizedPayload),
            ("$rawPayload", rawPayload),
            ("$occurredAtUtc", FormatTimestamp(occurredAt)));
    }

    public async Task InsertHealthEventAsync(
        string id,
        DateTimeOffset occurredAt,
        string scopeType = "Account",
        string scopeId = "account-1")
    {
        await ExecuteAsync(
            """
            INSERT INTO HealthEvents (Id, ScopeType, ScopeId, NewState, OccurredAtUtc)
            VALUES ($id, $scopeType, $scopeId, 'Degraded', $occurredAtUtc);
            """,
            ("$id", id),
            ("$scopeType", scopeType),
            ("$scopeId", scopeId),
            ("$occurredAtUtc", FormatTimestamp(occurredAt)));
    }

    public async Task InsertModelAsync(
        string id,
        string providerProfileId = "provider-1",
        string displayName = "Test model",
        string backend = "OpenCode")
    {
        await ExecuteAsync(
            """
            INSERT INTO Models
                (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState,
                 Provenance, IsEnabled, Health, DiscoveredAtUtc)
            VALUES ($id, $backend, $providerProfileId, $id, $displayName, 'Supported',
                    'ProviderReported', 1, 'Healthy', $timestamp);
            """,
            ("$id", id),
            ("$backend", backend),
            ("$providerProfileId", providerProfileId),
            ("$displayName", displayName),
            ("$timestamp", FormatTimestamp(SeedTimestamp)));
    }

    public async Task InsertQuotaSnapshotAsync(
        string id,
        DateTimeOffset capturedAt,
        string accountId = "account-1",
        string? modelId = null,
        string? bucket = null)
    {
        await ExecuteAsync(
            """
            INSERT INTO QuotaSnapshots
                (Id, AccountId, ModelId, Bucket, Freshness, Source, CapturedAtUtc)
            VALUES ($id, $accountId, $modelId, $bucket, 'Fresh', 'test', $capturedAtUtc);
            """,
            ("$id", id),
            ("$accountId", accountId),
            ("$modelId", modelId),
            ("$bucket", bucket),
            ("$capturedAtUtc", FormatTimestamp(capturedAt)));
    }

    public async Task InsertProjectLockAsync(
        string id,
        string canonicalRootPath,
        string executionId,
        string projectId = "project-1", string applicationInstanceId = "instance-1", long processGeneration = 1)
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
            VALUES ($id, $projectId, $canonicalRootPath, $executionId, $owner,
                    $generation, $acquiredAtUtc, NULL, NULL);
            """,
            ("$id", id),
            ("$projectId", projectId),
            ("$canonicalRootPath", normalizedRootPath),
            ("$executionId", executionId),
            ("$owner", applicationInstanceId),
            ("$generation", processGeneration),
            ("$acquiredAtUtc", FormatTimestamp(SeedTimestamp)));
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

    public void Dispose()
    {
        TestSqlitePool.Clear(Factory);
        _directory.Dispose();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
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
}

using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Retention;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.Retention;

public sealed class RetentionService : IRetentionService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IOptions<RetentionOptions> _retentionOptions;
    private readonly StorageOptions _storageOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationInstanceGuard? _instanceGuard;

    public RetentionService(
        ISqliteConnectionFactory connectionFactory,
        IOptions<RetentionOptions> retentionOptions,
        StorageOptions storageOptions,
        TimeProvider? timeProvider = null,
        IApplicationInstanceGuard? instanceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(retentionOptions);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _connectionFactory = connectionFactory;
        _retentionOptions = retentionOptions;
        _storageOptions = storageOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instanceGuard = instanceGuard;
    }

    public async Task<RetentionRunReport> RunAsync(CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = _timeProvider.GetUtcNow();
        var options = _retentionOptions.Value;
        var categories = new List<RetentionCategoryResult>();

        await using (var connection = await _connectionFactory
                         .OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            using var transaction = connection.BeginTransaction();

            categories.Add(new RetentionCategoryResult(
                RetentionCategories.RawBackendEvents,
                await DeleteExecutionEventsAsync(connection, transaction, startedAt, options, cancellationToken)
                    .ConfigureAwait(false),
                0,
                0));

            categories.Add(new RetentionCategoryResult(
                RetentionCategories.HealthAuditTransitions,
                await DeleteHealthEventsAsync(connection, transaction, startedAt, options, cancellationToken)
                    .ConfigureAwait(false),
                0,
                0));

            categories.Add(new RetentionCategoryResult(
                RetentionCategories.QuotaSnapshots,
                await DeleteQuotaSnapshotsAsync(connection, transaction, startedAt, options, cancellationToken)
                    .ConfigureAwait(false),
                0,
                0));

            categories.Add(new RetentionCategoryResult(
                RetentionCategories.QuotaSnapshotsDownsampled,
                await DownsampleQuotaSnapshotsAsync(connection, transaction, startedAt, options, cancellationToken)
                    .ConfigureAwait(false),
                0,
                0));

            transaction.Commit();
        }

        var dataRoot = ResolveDataRoot(_storageOptions);

        categories.Add(DeleteExpiredFiles(
            AppDataPaths.GetLogsDirectory(dataRoot),
            startedAt.AddDays(-options.ProcessServerLogsRetentionDays),
            RetentionCategories.ProcessServerLogs,
            cancellationToken));

        categories.Add(DeleteExpiredFiles(
            AppDataPaths.GetDiagnosticBundlesDirectory(dataRoot),
            startedAt.AddDays(-options.DiagnosticBundlesRetentionDays),
            RetentionCategories.DiagnosticBundles,
            cancellationToken));

        return new RetentionRunReport(startedAt, _timeProvider.GetUtcNow(), categories);
    }

    private static async Task<int> DeleteExecutionEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        var activeStates = ExecutionStateMachine.NonTerminalStates
            .Select(state => state.ToString())
            .ToArray();

        var placeholders = string.Join(
            ", ",
            activeStates.Select((_, index) => $"$activeState{index}"));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM ExecutionEvents
            WHERE OccurredAtUtc < $cutoff
              AND ExecutionId NOT IN (
                  SELECT Id FROM Executions WHERE State IN ({placeholders})
              );
            """;
        command.Parameters.AddWithValue(
            "$cutoff",
            SqliteRepositorySupport.FormatTimestamp(now.AddDays(-options.RawBackendEventsRetentionDays)));

        for (var index = 0; index < activeStates.Length; index++)
        {
            command.Parameters.AddWithValue($"$activeState{index}", activeStates[index]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteHealthEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM HealthEvents WHERE OccurredAtUtc < $cutoff;";
        command.Parameters.AddWithValue(
            "$cutoff",
            SqliteRepositorySupport.FormatTimestamp(
                now.AddDays(-options.HealthAuditTransitionsRetentionDays)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteQuotaSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM QuotaSnapshots WHERE CapturedAtUtc < $cutoff;";
        command.Parameters.AddWithValue(
            "$cutoff",
            SqliteRepositorySupport.FormatTimestamp(
                now.AddDays(-options.QuotaSnapshotsRetentionDays)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DownsampleQuotaSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        RetentionOptions options,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM QuotaSnapshots
            WHERE CapturedAtUtc < $downsampleCutoff
              AND Id NOT IN (
                  SELECT Id FROM (
                      SELECT Id,
                             ROW_NUMBER() OVER (
                                 PARTITION BY AccountId, COALESCE(ModelId, ''), COALESCE(Bucket, ''),
                                              substr(CapturedAtUtc, 1, 10)
                                 ORDER BY CapturedAtUtc DESC, Id DESC
                             ) AS RowNumber
                      FROM QuotaSnapshots
                      WHERE CapturedAtUtc < $downsampleCutoff
                  )
                  WHERE RowNumber = 1
              );
            """;
        command.Parameters.AddWithValue(
            "$downsampleCutoff",
            SqliteRepositorySupport.FormatTimestamp(
                now.AddDays(-options.QuotaSnapshotsDownsampleDays)));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RetentionCategoryResult DeleteExpiredFiles(
        string directory,
        DateTimeOffset cutoff,
        string category,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return new RetentionCategoryResult(category, 0, 0, 0);
        }

        var cutoffUtc = cutoff.UtcDateTime;
        var deleted = 0;
        var skipped = 0;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                entries = RetentionFileSystem.ReadDirectory(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    RetentionFileSystem.EnsureNoReparsePoints(entry);
                    var attributes = File.GetAttributes(entry);
                    // Preserve the existing hidden/system exclusion for individual retained files.
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    if (File.GetLastWriteTimeUtc(entry) >= cutoffUtc) continue;
                    RetentionFileSystem.EnsureNoReparsePoints(entry);
                    File.Delete(entry);
                    deleted++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        return new RetentionCategoryResult(category, 0, deleted, skipped);
    }

    private static string ResolveDataRoot(StorageOptions options)
    {
        return string.IsNullOrWhiteSpace(options.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(options.AppDataDirectory);
    }
}

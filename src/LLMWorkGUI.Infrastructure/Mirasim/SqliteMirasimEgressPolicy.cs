using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Mirasim;

/// <summary>Current metadata and content-free policy audit in the same SQLite writer transaction.</summary>
public sealed class SqliteMirasimEgressPolicy(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock) : IMirasimEgressPolicy
{
    public async Task<string> ValidateAsync(ProjectProviderContext context, string rootPath, CancellationToken cancellationToken)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var rows = await ReadAllowedAsync(connection, transaction, context, rootPath, cancellationToken).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Fingerprint(rows);
    }

    public async Task AuthorizeAsync(MirasimEgressOperation operation, CancellationToken cancellationToken)
    {
        guard.EnsureSupervisorPermitted();
        if (operation.Operation is not ("SessionCreate" or "SessionContinue" or "Turn")
            || !Uri.TryCreate(operation.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "http" || !MirasimOptions.IsLoopbackHostname(endpoint.Host)
            || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new MirasimEgressPolicyException();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var rows = await ReadAllowedAsync(connection, transaction, operation.Context, operation.RootPath, cancellationToken).ConfigureAwait(false);
        if (operation.ExpectedPolicyFingerprint != Fingerprint(rows)) throw new MirasimEgressPolicyException();
        if (operation.Operation is "SessionCreate" or "SessionContinue")
        {
            await using var ownership = connection.CreateCommand(); ownership.Transaction = transaction;
            ownership.CommandText = "SELECT COUNT(*) FROM ProjectLocks WHERE CanonicalRootPath=$root COLLATE NOCASE AND ReleasedAtUtc IS NULL";
            ownership.Parameters.AddWithValue("$root", CanonicalRoot(operation.RootPath));
            if (Convert.ToInt64(await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0) throw new MirasimEgressPolicyException();
        }
        if (rows.Profile.BaseUrl is { } configured
            && (!Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) || configuredUri != endpoint))
            throw new MirasimEgressPolicyException();
        var now = clock.GetUtcNow().ToString("O");
        var metadata = JsonSerializer.Serialize(new
        {
            operation.Operation, actor = "PrimaryLocalSupervisor", authorityInstanceId = guard.InstanceId,
            projectIdSha256 = Hash(rows.Project.Id), providerProfileIdSha256 = Hash(rows.Profile.Id),
            rootSha256 = Hash(CanonicalRoot(operation.RootPath)), endpointSha256 = Hash(endpoint.AbsoluteUri),
            sessionSha256 = Hash(operation.SessionKey), harnessSha256 = Hash(operation.Harness), modelSha256 = Hash(operation.ModelId),
            executionSha256 = operation.ExecutionId is null ? null : Hash(operation.ExecutionId),
            bodySha256 = Hash(operation.Body), classification = rows.Project.DataClassification.ToString(),
            policySha256 = Fingerprint(rows),
            nativeIdentityConfirmed = false
        });
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ActivityEvents (Id,OccurredAtUtc,Kind,Role,State,Source,TitleRedacted,DescriptionRedacted,IngestedAtUtc)
            VALUES ($id,$now,'System','policy','Completed','Synthetic','Mirasim: локальная policy проверена',$metadata,$now)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D")); command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$metadata", metadata);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(Project Project, ProviderProfile Profile)> ReadAllowedAsync(SqliteConnection connection,
        SqliteTransaction transaction, ProjectProviderContext context, string rootPath, CancellationToken token)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.ProjectId) || string.IsNullOrWhiteSpace(context.ProviderProfileId))
            throw new MirasimEgressPolicyException();
        Project project; ProviderProfile profile;
        await using (var command = Command(connection, transaction,
            $"SELECT {SqliteProjectRepository.SelectColumns} FROM Projects WHERE Id=$id", context.ProjectId))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
            project = SqliteProjectRepository.Read(reader);
        }
        await using (var command = Command(connection, transaction,
            $"SELECT {SqliteProviderProfileRepository.SelectColumns} FROM ProviderProfiles WHERE Id=$id", context.ProviderProfileId))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
            profile = SqliteProviderProfileRepository.ReadProfile(reader);
        }
        var root = CanonicalRoot(rootPath);
        if (!Directory.Exists(root) || !string.Equals(root, CanonicalRoot(project.RootPath), StringComparison.OrdinalIgnoreCase)
            || project.DataClassification == DataClassification.Restricted
            || !ProviderDataPolicy.Evaluate(project.DataClassification, context.ProviderProfileId, profile, BackendType.Mirasim).IsAllowed)
            throw new MirasimEgressPolicyException();
        return (project, profile);
    }

    private static string CanonicalRoot(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new MirasimEgressPolicyException();
            return ProjectLock.CanonicalizeRoot(root);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new MirasimEgressPolicyException(); }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, string id)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id); return command;
    }
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    internal static string Fingerprint((Project Project, ProviderProfile Profile) rows) => Hash(JsonSerializer.Serialize(new { rows.Project, rows.Profile }));
}

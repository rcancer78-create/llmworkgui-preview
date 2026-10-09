using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows.Channels;

/// <summary>Stored review admission consumed only by the actual project-bound HTTP request.</summary>
public sealed class SqliteWorkflowReviewEgressPolicy(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock) : IWorkflowReviewEgressPolicy
{
    public async Task<string> ValidateAsync(WorkflowReviewEgressContext context, string endpoint, string prompt,
        string? reasoningEffort, string? nativeSessionId, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var ready = await ReadAsync(connection, transaction, context, endpoint, prompt, reasoningEffort, nativeSessionId, token).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return ready.NativeModel;
    }

    public async Task AuthorizeAsync(WorkflowReviewEgressContext context, string endpoint, string body,
        string? nativeSessionId, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        using var document = JsonDocument.Parse(body);
        var json = document.RootElement;
        var messages = json.GetProperty("messages");
        if (!json.GetProperty("stream").GetBoolean() || messages.GetArrayLength() != 1
            || messages[0].GetProperty("role").GetString() != "user") throw new WorkflowReviewEgressException();
        var prompt = messages[0].GetProperty("content").GetString() ?? throw new WorkflowReviewEgressException();
        var effort = json.TryGetProperty("reasoning_effort", out var e) ? e.GetString() : null;
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var ready = await ReadAsync(connection, transaction, context, endpoint, prompt, effort, nativeSessionId, token).ConfigureAwait(false);
        if (json.GetProperty("model").GetString() != ready.NativeModel) throw new WorkflowReviewEgressException();
        var authorizedAt = clock.GetUtcNow().ToString("O");
        await using (var command = Command(connection, transaction, """
            UPDATE Executions SET State='Running',StartedAtUtc=$now WHERE Id=$execution AND State='Queued' AND EndedAtUtc IS NULL;
            """, ("$execution", context.ExecutionId), ("$now", authorizedAt)))
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new WorkflowReviewEgressException();
        await using (var command = Command(connection, transaction, """
            INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),0)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),
              'WorkflowReviewTransportAuthorized',$payload,$classification,$now);
            """, ("$id", Guid.NewGuid().ToString("D")), ("$execution", context.ExecutionId),
            ("$payload", JsonSerializer.Serialize(new { bodySha256 = Hash(body), policySha256 = ready.Fingerprint,
                authorityInstanceId = guard.InstanceId, nativeIdentityConfirmed = false })),
            ("$classification", ready.Classification.ToString()), ("$now", authorizedAt)))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task<Ready> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowReviewEgressContext context, string endpoint, string prompt, string? effort, string? nativeSession, CancellationToken token)
    {
        if (context is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || !StarCliProxyOptions.IsLoopbackHostname(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new WorkflowReviewEgressException();
        string project, root, runId, artifactId, artifactHash;
        await using (var command = Command(connection, transaction, """
            SELECT s.ProjectId,s.WorkspaceRootPath,b.WorkflowRunId,b.ReviewedArtifactId,b.ReviewedArtifactHash,s.NativeSessionId
            FROM Executions e JOIN Sessions s ON s.Id=e.SessionId JOIN WorkflowReviewExecutions b ON b.ExecutionId=e.Id
            JOIN Routes r ON r.Id=e.RequestedRouteId
            WHERE e.Id=$execution AND e.State='Queued' AND e.EndedAtUtc IS NULL AND e.ObservedRouteId IS NULL
              AND s.ActiveExecutionId=e.Id AND s.State='Starting' AND s.Backend='StarCliProxy'
              AND b.SessionId=s.Id AND b.WorkflowRunId=s.WorkflowRunId AND b.ReviewerRole=s.Role
              AND b.IsReadOnly=1 AND b.ObservedRouteId IS NULL
              AND b.RequestedRouteId=r.Id AND r.Id=$route AND b.StageId=$stage AND b.ReviewerRole=$role
              AND s.ProviderProfileId=r.ProviderProfileId AND s.AccountId=r.AccountId AND s.ModelId=r.ModelId
              AND s.ReasoningEffort IS r.ReasoningEffort AND s.SpeedMode IS r.SpeedMode AND s.ExecutionMode IS r.ExecutionMode
            """, ("$execution", context.ExecutionId), ("$route", context.RouteId), ("$stage", context.StageId), ("$role", context.ReviewerRole)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new WorkflowReviewEgressException();
            project = reader.GetString(0); root = reader.GetString(1); runId = reader.GetString(2);
            artifactId = reader.GetString(3); artifactHash = reader.GetString(4);
            if ((reader.IsDBNull(5) ? null : reader.GetString(5)) != nativeSession) throw new WorkflowReviewEgressException();
        }
        var rows = await SqliteEgressPolicyReader.ReadAsync(connection, transaction, project, context.RouteId, token).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(rows.Project.RootPath)) throw new WorkflowReviewEgressException();
        var canonicalRoot = ProjectLock.CanonicalizeRoot(root);
        if (!Directory.Exists(canonicalRoot) || !string.Equals(canonicalRoot, ProjectLock.CanonicalizeRoot(rows.Project.RootPath), StringComparison.OrdinalIgnoreCase)
            || rows.Project.DataClassification == DataClassification.Restricted || rows.Route.Binding.Backend != BackendType.StarCliProxy
            || rows.Route.Binding.ReasoningEffort != effort || rows.Route.Binding.SpeedMode is not null || rows.Route.Binding.ExecutionMode is not null
            || rows.Profile.BaseUrl is not { } pinned || !Uri.TryCreate(pinned, UriKind.Absolute, out var saved) || saved != uri
            || !ProviderDataPolicy.Evaluate(rows.Project.DataClassification, rows.Profile.Id, rows.Profile, BackendType.StarCliProxy).IsAllowed)
            throw new WorkflowReviewEgressException();
        var run = await SqliteWorkflowRunRepository.GetByIdAsync(connection, transaction, runId, token).ConfigureAwait(false);
        var artifact = run?.Artifacts.FirstOrDefault(a => a.ArtifactId == artifactId);
        if (run is null || run.IsTerminal || run.ProjectId != project || run.CurrentStageId != context.StageId || artifact is null
            || artifact.HashSha256 != artifactHash || artifact.Classification == DataClassification.Restricted
            || WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, context.StageId, artifact.Kind)?.ArtifactId != artifactId)
            throw new WorkflowReviewEgressException();
        var classification = (DataClassification)Math.Max((int)rows.Project.DataClassification, (int)artifact.Classification);
        if (!Enum.IsDefined(classification) || classification > rows.Route.MaxDataClass || classification > rows.Profile.MaxDataClass)
            throw new WorkflowReviewEgressException();
        await using (var command = Command(connection, transaction,
            "SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE ExecutionId=$execution AND EventKind='WorkflowReviewEgressPrepared'",
            ("$execution", context.ExecutionId)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new WorkflowReviewEgressException();
            using var prepared = JsonDocument.Parse(reader.GetString(0)); var data = prepared.RootElement;
            if (data.GetProperty("promptSha256").GetString() != Hash(prompt) || data.GetProperty("policySha256").GetString() != rows.Fingerprint
                || data.GetProperty("artifactClass").GetString() != artifact.Classification.ToString()
                || await reader.ReadAsync(token).ConfigureAwait(false)) throw new WorkflowReviewEgressException();
        }
        var query = NativeGatewayRouteQuery.Sql.Replace("'NativeGateway'", "'StarCliProxy'", StringComparison.Ordinal)
            .Replace("IN ('StarCliProxy',p.Id)", "IN ('StarCliProxy','star-cliproxy',p.Id)", StringComparison.Ordinal)
            .Replace("r.ReasoningEffort IS NULL AND r.SpeedMode", "r.SpeedMode", StringComparison.Ordinal)
            .Replace("WHERE s.AccountId=a.Id AND (", "WHERE s.AccountId=a.Id AND e.Id!=$execution AND (", StringComparison.Ordinal);
        string model;
        await using (var command = Command(connection, transaction, query + " AND r.Id=$route",
            ("$project", project), ("$root", canonicalRoot), ("$now", clock.GetUtcNow().ToString("O")),
            ("$execution", context.ExecutionId), ("$route", context.RouteId)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new WorkflowReviewEgressException();
            model = reader.GetString(4);
            if (string.IsNullOrWhiteSpace(model) || model.Equals("auto", StringComparison.OrdinalIgnoreCase)) throw new WorkflowReviewEgressException();
        }
        return new(model, rows.Fingerprint, classification);
    }
    private sealed record Ready(string NativeModel, string Fingerprint, DataClassification Classification);
    private static string Hash(string body) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(body)));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

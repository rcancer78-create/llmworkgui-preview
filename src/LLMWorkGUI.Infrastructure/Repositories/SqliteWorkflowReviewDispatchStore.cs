using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Application.Workflows.Declarative;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Infrastructure.Providers;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteWorkflowReviewDispatchStore(ISqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IWorkflowReviewDispatchStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<bool> TryAdmitAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default) => TryAdmitCoreAsync(session, execution, evidence, null, cancellationToken);

    public Task<bool> TryAdmitWithPromptAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var hash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(prompt)));
        return TryAdmitCoreAsync(session, execution, evidence, hash, cancellationToken);
    }

    private async Task<bool> TryAdmitCoreAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        string? promptHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(execution, evidence);
        if (session.Id != execution.SessionId || session.WorkflowRunId != evidence.WorkflowRunId ||
            session.ActiveExecutionId != execution.Id || session.State != SessionState.Starting ||
            execution.State != ExecutionState.Queued || evidence.ObservedRouteId is not null)
            throw new ArgumentException("Reviewer admission must contain one unobserved session, execution and binding.");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Take the writer reservation before checking the existing unique role/artifact key. Concurrent
        // callers cannot both pass this read; migration 017's state-aware trigger is a second guard.
        using var transaction = connection.BeginTransaction(deferred: false);
        // The service has awaited blob/project reads since its preflight. Revalidate against the
        // same writer transaction that admits the request, using the repository's artifact ordering.
        var run = await SqliteWorkflowRunRepository.GetByIdAsync(connection, transaction,
            evidence.WorkflowRunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.IsTerminal || run.CurrentStageId != evidence.StageId) return false;
        var artifact = run.Artifacts.FirstOrDefault(item => item.ArtifactId == evidence.ReviewedArtifactId);
        if (artifact is null || artifact.HashSha256 != evidence.ReviewedArtifactHash) return false;
        var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, evidence.StageId, artifact.Kind);
        if (current?.ArtifactId != artifact.ArtifactId || current.HashSha256 != evidence.ReviewedArtifactHash) return false;
        // Revalidate classification while holding the same writer transaction as admission. A profile,
        // route or project changed after the service's awaited preflight cannot widen this request.
        await using (var policy = connection.CreateCommand())
        {
            policy.Transaction = transaction;
            policy.CommandText = """
                SELECT 1 FROM Projects pr
                JOIN Routes r ON r.Id=$route
                JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
                WHERE pr.Id=$project AND pr.Id=$runProject
                  AND r.Backend=$backend AND r.ProviderProfileId=$profile
                  AND r.AccountId=$account AND r.ModelId=$model
                  AND r.IsEnabled=1 AND p.IsEnabled=1
                  AND pr.DataClassification IN ('PublicSource','PrivateSource')
                  AND $artifactClass IN ('PublicSource','PrivateSource')
                  AND p.MaxDataClass IN ('PublicSource','PrivateSource','Restricted')
                  AND r.MaxDataClass IN ('PublicSource','PrivateSource','Restricted')
                  AND ((pr.DataClassification='PublicSource' AND $artifactClass='PublicSource')
                    OR (p.MaxDataClass IN ('PrivateSource','Restricted') AND r.MaxDataClass IN ('PrivateSource','Restricted')));
                """;
            policy.Parameters.AddWithValue("$route", evidence.RequestedRouteId);
            policy.Parameters.AddWithValue("$project", session.ProjectId);
            policy.Parameters.AddWithValue("$runProject", run.ProjectId);
            policy.Parameters.AddWithValue("$backend", session.Binding.Backend.ToString());
            policy.Parameters.AddWithValue("$profile", session.Binding.ProviderProfileId);
            policy.Parameters.AddWithValue("$account", session.Binding.AccountId);
            policy.Parameters.AddWithValue("$model", session.Binding.ModelId);
            policy.Parameters.AddWithValue("$artifactClass", artifact.Classification.ToString());
            if (await policy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return false;
        }
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT 1 FROM WorkflowReviewExecutions b JOIN Executions e ON e.Id = b.ExecutionId
                WHERE b.WorkflowRunId = $run AND b.StageId = $stage AND b.ReviewerRole = $role
                  AND b.ReviewedArtifactHash = $hash AND b.IsReadOnly = $readOnly
                  AND e.State NOT IN ('Failed', 'Cancelled') LIMIT 1;
                """;
            duplicate.Parameters.AddWithValue("$run", evidence.WorkflowRunId);
            duplicate.Parameters.AddWithValue("$stage", evidence.StageId);
            duplicate.Parameters.AddWithValue("$role", evidence.ReviewerRole);
            duplicate.Parameters.AddWithValue("$hash", evidence.ReviewedArtifactHash);
            duplicate.Parameters.AddWithValue("$readOnly", evidence.IsReadOnly ? 1 : 0);
            if (await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                return false;
            duplicate.CommandText = """
                SELECT COUNT(*), COALESCE(MAX(CASE WHEN b.ExecutionId=$previous AND e.State IN ('Failed','Cancelled') THEN 1 ELSE 0 END),0)
                FROM WorkflowReviewExecutions b JOIN Executions e ON e.Id=b.ExecutionId
                WHERE b.WorkflowRunId=$run AND b.StageId=$stage AND b.ReviewerRole=$role
                  AND b.ReviewedArtifactHash=$hash AND b.IsReadOnly=$readOnly;
                """;
            SqliteRepositorySupport.AddNullable(duplicate, "$previous", execution.RetryOfExecutionId);
            await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var hasPrevious = reader.GetInt64(0) > 0;
            if (hasPrevious ? reader.GetInt64(1) != 1 : execution.RetryOfExecutionId is not null)
                throw new ArgumentException("Reviewer retry must name a matching known Failed/Cancelled predecessor.");
        }

        await SqliteSessionRepository.UpsertAsync(connection, transaction, session, cancellationToken).ConfigureAwait(false);
        await SqliteExecutionRepository.UpsertAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        await SqliteWorkflowReviewEvidenceRepository.SaveAsync(connection, transaction, evidence,
            _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (promptHash is not null)
        {
            var rows = await SqliteEgressPolicyReader.ReadAsync(connection, transaction, session.ProjectId,
                evidence.RequestedRouteId, cancellationToken).ConfigureAwait(false);
            await using var prepared = connection.CreateCommand(); prepared.Transaction = transaction;
            prepared.CommandText = """
                INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
                VALUES ($id,$execution,1,'WorkflowReviewEgressPrepared',$payload,$classification,$now);
                """;
            prepared.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            prepared.Parameters.AddWithValue("$execution", execution.Id);
            prepared.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new
                { promptSha256 = promptHash, policySha256 = rows.Fingerprint, artifactClass = artifact.Classification.ToString(), nativeIdentityConfirmed = false }));
            prepared.Parameters.AddWithValue("$classification", ((DataClassification)Math.Max((int)rows.Project.DataClassification, (int)artifact.Classification)).ToString());
            prepared.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
            await prepared.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task CompleteAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default) => CompleteWithResponseAsync(session, execution, evidence, null, cancellationToken);

    public async Task CompleteWithResponseAsync(Session session, Execution execution, ReviewerExecutionEvidence evidence,
        WorkflowModelResponse? response, CancellationToken cancellationToken = default)
    {
        Validate(execution, evidence);
        if (session.Id != execution.SessionId || session.WorkflowRunId != evidence.WorkflowRunId)
            throw new ArgumentException("Reviewer completion must belong to its admitted session and run.");
        var uncertain = execution.State is not (ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled);
        if (session.State != (uncertain ? SessionState.Ambiguous : SessionState.Closed)
            || session.ActiveExecutionId != (uncertain ? execution.Id : null))
            throw new ArgumentException("Reviewer session state must preserve uncertain execution ownership.");
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        // Completion receives the service's pre-dispatch snapshot. Preserve the later atomic HTTP
        // authorization timestamp; that event is not native receipt or terminal proof.
        object? authorizedStart;
        await using (var start = connection.CreateCommand())
        {
            start.Transaction = transaction;
            start.CommandText = """
                SELECT StartedAtUtc FROM Executions WHERE Id=$execution AND SessionId=$session
                AND EXISTS(SELECT 1 FROM ExecutionEvents WHERE ExecutionId=$execution AND EventKind='WorkflowReviewTransportAuthorized');
                """;
            start.Parameters.AddWithValue("$execution", execution.Id); start.Parameters.AddWithValue("$session", session.Id);
            authorizedStart = await start.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteExecutionRepository.UpsertAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        if (authorizedStart is string actualStart)
        {
            await using var start = connection.CreateCommand(); start.Transaction = transaction;
            start.CommandText = "UPDATE Executions SET StartedAtUtc=$start WHERE Id=$execution AND SessionId=$session";
            start.Parameters.AddWithValue("$start", actualStart); start.Parameters.AddWithValue("$execution", execution.Id); start.Parameters.AddWithValue("$session", session.Id);
            await start.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteWorkflowReviewEvidenceRepository.UpdateObservedAsync(connection, transaction, evidence,
            _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await using (var updateSession = connection.CreateCommand())
        {
            updateSession.Transaction = transaction;
            updateSession.CommandText = """
                UPDATE Sessions SET State=$state, ReconciliationOutcome=$reconciliation,
                    ActiveExecutionId=$active, LastEventAtUtc=$updated, NativeSessionId=$native
                WHERE Id=$session AND WorkflowRunId=$run AND ActiveExecutionId=$execution;
                """;
            updateSession.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(session.State));
            updateSession.Parameters.AddWithValue("$reconciliation", SqliteRepositorySupport.FormatEnum(session.ReconciliationOutcome));
            SqliteRepositorySupport.AddNullable(updateSession, "$active", session.ActiveExecutionId);
            SqliteRepositorySupport.AddNullable(updateSession, "$native", session.NativeSessionId);
            updateSession.Parameters.AddWithValue("$updated", SqliteRepositorySupport.FormatTimestamp(session.LastEventAt));
            updateSession.Parameters.AddWithValue("$session", session.Id);
            updateSession.Parameters.AddWithValue("$run", evidence.WorkflowRunId);
            updateSession.Parameters.AddWithValue("$execution", execution.Id);
            if (await updateSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Reviewer completion no longer owns the admitted session.");
        }
        if (response is not null)
            await SqliteWorkflowReviewResponseRepository.InsertAsync(connection, transaction, execution.Id,
                session.NativeSessionId, response, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(Execution execution, ReviewerExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(evidence);
        if (execution.Id != evidence.ExecutionId || execution.SessionId != evidence.SessionId ||
            execution.RequestedRouteId != evidence.RequestedRouteId ||
            execution.ObservedRouteId != evidence.ObservedRouteId || execution.State != evidence.ExecutionState)
            throw new ArgumentException("Reviewer execution and binding must describe the same observed outcome.");
    }
}

using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

public sealed partial class SqliteCursorAcpExecutionJournal
{
    public async Task<bool> AuthorizePromptDispatchAsync(CursorAcpJournalEntry entry, string projectId, string rootPath,
        CursorAcpPromptRequest request, string? writerLockId, long processGeneration, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        if (request.SessionId != entry.NativeSessionId || request.ClientRequestId != entry.ClientRequestId
            || request.Model != entry.Route.NativeModelId || !request.RequireModelAcknowledgement
            || request.ModeId is not ("ask" or "plan" or "agent") || processGeneration <= 0
            || !Guid.TryParse(request.ClientRequestId, out _)
            || request.PromptHash != CursorAcpPromptRequest.ComputePromptHash(request.Prompt)) return false;
        var canonicalRoot = ProjectLock.CanonicalizeRoot(rootPath);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        string routeClass, profileClass;
        await using (var check = Command(connection, transaction, RoutesSql + " AND r.Id=$route", ("$now", Now()), ("$route", entry.Route.Id)))
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || ReadRoute(reader) != entry.Route
                || !reader.IsDBNull(5) && reader.GetString(5) != request.ModeId) return false;
            routeClass = reader.GetString(6); profileClass = reader.GetString(7);
        }
        await using (var capacity = Command(connection, transaction, """
            SELECT COUNT(*) FROM Accounts a WHERE a.Id=$account AND a.MaxConcurrentExecutions>=1
                AND a.MaxConcurrentExecutions>=(SELECT COUNT(*) FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
                    WHERE s.AccountId=a.Id AND (e.EndedAtUtc IS NULL
                        OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
                        OR s.ActiveExecutionId=e.Id
                        OR EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL)));
            """, ("$account", entry.Route.AccountId)))
            if (Convert.ToInt64(await capacity.ExecuteScalarAsync(cancellationToken)) != 1) return false;
        await using (var check = Command(connection, transaction, """
            SELECT p.RootPath,p.DataClassification,s.WorkspaceRootPath
            FROM Executions e JOIN Sessions s ON s.Id=e.SessionId JOIN Projects p ON p.Id=s.ProjectId
            JOIN ClientRequests c ON c.Id=e.ClientRequestId AND c.ExecutionId=e.Id AND c.SessionId=s.Id
            WHERE e.Id=$execution AND e.SessionId=$session AND e.ClientRequestId=$request AND e.RequestedRouteId=$route
                AND e.State='SessionConfirmed' AND e.EndedAtUtc IS NULL
                AND c.RequestedRouteId=$route AND c.PromptHash=$hash AND s.ProjectId=$project
                AND s.Backend='CursorAcp' AND s.NativeSessionId=$native AND s.State='Active' AND s.ActiveExecutionId=e.Id
                AND s.ProviderProfileId=$profile AND s.AccountId=$account AND s.ModelId=$model
                AND s.ExecutionMode=$mode AND s.ReasoningEffort IS NULL AND s.SpeedMode IS NULL
                AND NOT EXISTS (SELECT 1 FROM ExecutionEvents v WHERE v.ExecutionId=e.Id AND v.EventKind='CursorPromptDispatch');
            """, ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.ClientRequestId),
            ("$route", entry.Route.Id), ("$hash", request.PromptHash), ("$project", projectId), ("$native", entry.NativeSessionId),
            ("$profile", entry.Route.ProviderProfileId), ("$account", entry.Route.AccountId), ("$model", entry.Route.ModelId), ("$mode", request.ModeId!)))
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)
                || !SameRoot(reader.GetString(0), canonicalRoot) || !SameRoot(reader.GetString(2), canonicalRoot)) return false;
            var classification = reader.GetString(1);
            if (classification == "Restricted" || !Allows(routeClass, classification) || !Allows(profileClass, classification)) return false;
        }
        if (request.ModeId == "agent")
        {
            if (string.IsNullOrWhiteSpace(writerLockId)) return false;
            await using var check = Command(connection, transaction, """
                SELECT CanonicalRootPath FROM ProjectLocks WHERE Id=$lock AND ProjectId=$project AND ExecutionId=$execution
                    AND ApplicationInstanceId=$owner AND ProcessGeneration=$generation AND ReleasedAtUtc IS NULL;
                """, ("$lock", writerLockId), ("$project", projectId), ("$execution", entry.ExecutionId),
                ("$owner", instanceGuard.InstanceId), ("$generation", processGeneration));
            if (await check.ExecuteScalarAsync(cancellationToken) is not string lockedRoot || !SameRoot(lockedRoot, canonicalRoot)) return false;
        }
        else if (writerLockId is not null) return false;
        instanceGuard.EnsureSupervisorPermitted();
        await using (var audit = Command(connection, transaction, """
            INSERT INTO ExecutionEvents(Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            SELECT $id,$execution,COALESCE(MAX(Sequence)+1,0),'CursorPromptDispatch',
                $payload,'PrivateSource',$now
            FROM ExecutionEvents WHERE ExecutionId=$execution;
            """, ("$id", Guid.NewGuid().ToString("D")), ("$execution", entry.ExecutionId), ("$now", Now()),
            ("$payload", JsonSerializer.Serialize(new { phase = "LocalDispatchAuthorized", nativeDeliveryConfirmed = false, processGeneration }))))
            await audit.ExecuteNonQueryAsync(cancellationToken);
        instanceGuard.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    private static bool SameRoot(string actual, string expected) => string.Equals(ProjectLock.CanonicalizeRoot(actual), expected,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.OpenCode;

public sealed partial class SqliteOpenCodeExecutionJournal
{
    public async Task<bool> AuthorizePromptDispatchAsync(OpenCodeJournalEntry entry, string nativeSessionId,
        OpenCodePromptRequest request, Uri requestUri, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        if (entry.ProcessGeneration <= 0 || nativeSessionId != entry.NativeSessionId || request.Model != entry.Route.NativeModelId
            || request.Agent is not null || request.AdaptationAdmissionId is not null
            || !requestUri.IsAbsoluteUri || !requestUri.IsLoopback || requestUri.Scheme != "http"
            || requestUri.AbsolutePath != "/session/" + Uri.EscapeDataString(entry.NativeSessionId) + "/prompt_async"
            || !string.IsNullOrEmpty(requestUri.Fragment))
            return false;
        var parameters = requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parameters.Length != 1) return false;
        var directories = parameters
            .Select(pair => pair.Split('=', 2)).Where(pair => Uri.UnescapeDataString(pair[0]) == "directory")
            .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "").ToArray();
        if (directories.Length != 1 || string.IsNullOrWhiteSpace(directories[0])) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!await ValidateDispatchPolicyAsync(connection, transaction, entry, cancellationToken)) return false;
        await using (var check = Command(connection, transaction, """
            SELECT s.WorkspaceRootPath FROM Sessions s JOIN Executions e ON e.SessionId=s.Id
            JOIN ClientRequests c ON c.Id=e.ClientRequestId AND c.ExecutionId=e.Id AND c.SessionId=s.Id
            WHERE e.Id=$execution AND e.State='Running' AND e.EndedAtUtc IS NULL
                AND upper(c.PromptHash)=$hash
                AND NOT EXISTS (SELECT 1 FROM ExecutionEvents v WHERE v.ExecutionId=e.Id AND v.EventKind='OpenCodeTransport');
            """, ("$execution", entry.ExecutionId), ("$hash", hash)))
        {
            var root = await check.ExecuteScalarAsync(cancellationToken) as string;
            if (root is null || !string.Equals(ProjectLock.CanonicalizeRoot(root),
                    ProjectLock.CanonicalizeRoot(directories[0]), StringComparison.OrdinalIgnoreCase)) return false;
        }
        instanceGuard.EnsureSupervisorPermitted();
        var audit = await OpenCodeJournalEvents.AppendAsync(connection, transaction, entry.ExecutionId,
            entry.SessionId, entry.Route.Id, "OpenCodeTransport", ExecutionState.Running,
            "DispatchAuthorized", timeProvider.GetUtcNow(), cancellationToken, entry.ProcessGeneration);
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit();
        OpenCodeJournalEvents.Publish(activity, audit);
        return true;
    }

    private async Task<bool> ValidateDispatchPolicyAsync(SqliteConnection connection, SqliteTransaction transaction,
        OpenCodeJournalEntry entry, CancellationToken token, bool requireOwner = true)
    {
        OpenCodeStoredRoute route;
        string routeClass, profileClass;
        await using (var check = Command(connection, transaction, RoutesSql + " AND r.Id=$route",
            ("$now", Now()), ("$route", entry.Route.Id)))
        await using (var reader = await check.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return false;
            route = ReadRoute(reader); routeClass = reader.GetString(6); profileClass = reader.GetString(7);
        }
        if (route != entry.Route || string.IsNullOrWhiteSpace(route.NativeModelId)) return false;
        if (requireOwner)
        {
            await using var capacity = Command(connection, transaction, """
                SELECT COUNT(*) FROM Accounts a WHERE a.Id=$account AND a.MaxConcurrentExecutions>=1
                    AND a.MaxConcurrentExecutions>=(SELECT COUNT(*) FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
                        WHERE s.AccountId=a.Id AND (e.EndedAtUtc IS NULL
                            OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
                            OR s.ActiveExecutionId=e.Id
                            OR EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL)));
                """, ("$account", route.AccountId));
            if (Convert.ToInt64(await capacity.ExecuteScalarAsync(token)) != 1) return false;
        }
        await using var identity = Command(connection, transaction, """
            SELECT p.RootPath,p.DataClassification,s.WorkspaceRootPath,
                (SELECT l.CanonicalRootPath FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL LIMIT 1)
            FROM Executions e JOIN Sessions s ON s.Id=e.SessionId JOIN Projects p ON p.Id=s.ProjectId
            WHERE e.Id=$execution AND e.SessionId=$session AND e.ClientRequestId=$request AND e.RequestedRouteId=$route
                AND e.State IN ('SessionConfirmed','Running') AND e.EndedAtUtc IS NULL
                AND s.Backend='OpenCode' AND s.NativeSessionId=$native AND s.State='Active' AND s.ActiveExecutionId=e.Id
                AND s.ProviderProfileId=$profile AND s.AccountId=$account AND s.ModelId=$model
                AND s.ReasoningEffort IS NULL AND s.SpeedMode IS NULL AND s.ExecutionMode IS NULL
                AND EXISTS (SELECT 1 FROM ExecutionEvents v WHERE v.ExecutionId=e.Id AND v.EventKind='OpenCodeAdmission'
                    AND CASE WHEN json_valid(v.NormalizedRedactedPayloadJson)
                        THEN json_extract(v.NormalizedRedactedPayloadJson,'$.processGeneration') ELSE NULL END=$generation
                    AND $generation>0)
                AND EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ProjectId=s.ProjectId
                    AND ($requireOwner=0 OR ($generation>0 AND l.ApplicationInstanceId=$owner AND l.ProcessGeneration=$generation)) AND l.ReleasedAtUtc IS NULL);
            """, ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.ClientRequestId),
            ("$route", route.Id), ("$native", entry.NativeSessionId), ("$profile", route.ProviderProfileId),
            ("$account", route.AccountId), ("$model", route.ModelId), ("$owner", instanceGuard.InstanceId),
            ("$generation", entry.ProcessGeneration),
            ("$requireOwner", requireOwner ? 1 : 0));
        await using var row = await identity.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return false;
        var classification = row.GetString(1);
        return classification != "Restricted" && Allows(routeClass, classification) && Allows(profileClass, classification)
            && string.Equals(ProjectLock.CanonicalizeRoot(row.GetString(0)),
                ProjectLock.CanonicalizeRoot(row.GetString(2)), StringComparison.OrdinalIgnoreCase)
            && (!requireOwner || string.Equals(ProjectLock.CanonicalizeRoot(row.GetString(3)),
                ProjectLock.CanonicalizeRoot(row.GetString(2)), StringComparison.OrdinalIgnoreCase));
    }
}

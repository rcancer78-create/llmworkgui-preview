using System.Globalization;
using System.Text.Json;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.OpenCode;

internal sealed record OpenCodeJournalEvent(string Id, string ExecutionId, string SessionId, string RouteId,
    string Kind, ExecutionState State, string Reason, DateTimeOffset At);

internal static class OpenCodeJournalEvents
{
    public static async Task<OpenCodeJournalEvent> AppendAsync(SqliteConnection connection, SqliteTransaction transaction,
        string executionId, string sessionId, string routeId, string kind, ExecutionState state, string reason,
        DateTimeOffset now, CancellationToken token, long? processGeneration = null)
    {
        var item = new OpenCodeJournalEvent(Guid.NewGuid().ToString("D"), executionId, sessionId, routeId, kind, state, reason, now);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),-1)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),
                $kind,$payload,'PrivateSource',$now)
            """;
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$execution", executionId);
        command.Parameters.AddWithValue("$kind", kind);
        // Only enums and fixed reasons are persisted. No prompt, output, native path or exception text.
        command.Parameters.AddWithValue("$payload", processGeneration is null
            ? JsonSerializer.Serialize(new { state = state.ToString(), reason })
            : JsonSerializer.Serialize(new { state = state.ToString(), reason, processGeneration }));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token);
        return item;
    }

    public static void Publish(IActivityCenterService? activity, OpenCodeJournalEvent item, bool replay = false)
    {
        if (activity is null) return;
        try
        {
            var activityEvent = new ActivityEvent("opencode-journal:" + item.Id, item.At, ActivityEventKind.Execution,
                ActivityRoleNames.System, ActivityEvent.MapExecutionState(item.State), ActivityEventSource.Native,
                $"OpenCode: локальный статус {item.State}",
                $"Журнал приложения: {item.Reason}. Запрошенный маршрут: {item.RouteId}. Нативная идентичность ответа не подтверждена.",
                item.SessionId, item.ExecutionId);
            if (replay) activity.ReplayJournalEvent(activityEvent);
            else activity.Append(activityEvent);
        }
        catch (Exception) { /* Durable ExecutionEvents remain available for replay; observers cannot break native ownership. */ }
    }

    public static async Task<int> ReplayAsync(SqliteConnection connection, IActivityCenterService? activity, CancellationToken token)
    {
        if (activity is null) return 0;
        var events = new List<OpenCodeJournalEvent>();
        var invalidRows = 0;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ev.Id,e.Id,e.SessionId,e.RequestedRouteId,ev.EventKind,ev.NormalizedRedactedPayloadJson,ev.OccurredAtUtc
            FROM ExecutionEvents ev JOIN Executions e ON e.Id=ev.ExecutionId JOIN Sessions s ON s.Id=e.SessionId
            WHERE s.Backend='OpenCode' AND ev.EventKind IN ('OpenCodeAdmission','OpenCodeDispatch','OpenCodeTerminal','OpenCodeInterrupted','OpenCodeRecoveryAction','OpenCodeApproval')
            ORDER BY ev.OccurredAtUtc DESC,e.Id DESC,ev.Sequence DESC,ev.Id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", activity.Capacity);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                try
                {
                    using var payload = JsonDocument.Parse(reader.GetString(5));
                    if (!Enum.TryParse<ExecutionState>(payload.RootElement.GetProperty("state").GetString(), out var state) || !Enum.IsDefined(state))
                        throw new InvalidDataException("Invalid OpenCode journal event state.");
                    var reason = payload.RootElement.GetProperty("reason").GetString();
                    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 100 || reason.Any(char.IsControl))
                        throw new InvalidDataException("Invalid OpenCode journal event reason.");
                    events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        state, reason, DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException
                    or KeyNotFoundException or FormatException or InvalidCastException or ArgumentException)
                { invalidRows++; }
            }
        events.Reverse(); foreach (var item in events) Publish(activity, item, replay: true);
        if (invalidRows > 0)
        {
            try
            {
            activity.ReplayJournalEvent(ActivityEvent.SystemEvent("opencode-journal:invalid-events",
                "Некоторые события OpenCode не удалось восстановить",
                $"Повреждённых записей: {invalidRows}. Они сохранены в SQLite; остальные события загружены. Состояние выполнений и блокировки не изменены.",
                DateTimeOffset.UtcNow));
            }
            catch (Exception) { /* Durable rows and valid replay survive observer failures, as in Publish. */ }
        }
        return events.Count;
    }
}

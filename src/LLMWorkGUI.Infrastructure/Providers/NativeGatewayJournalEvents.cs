using System.Globalization;
using System.Text.Json;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

internal sealed record NativeGatewayJournalEvent(string Id, string ExecutionId, string SessionId,
    ExecutionState State, ExecutionFailureReason Failure, bool Interrupted, DateTimeOffset At);

internal static class NativeGatewayJournalEvents
{
    public static async Task<NativeGatewayJournalEvent> AppendAsync(SqliteConnection connection, SqliteTransaction transaction,
        string executionId, string sessionId, ExecutionState state, ExecutionFailureReason failure,
        bool interrupted, DateTimeOffset now, CancellationToken token, string? reasoningEffort = null)
    {
        var item = new NativeGatewayJournalEvent(Guid.NewGuid().ToString("D"), executionId, sessionId, state, failure, interrupted, now);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),-1)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),
                $kind,$payload,'PrivateSource',$now)
            """;
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$execution", executionId);
        command.Parameters.AddWithValue("$kind", interrupted ? "NativeGatewayInterrupted" : "NativeGatewayLifecycle");
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { state = state.ToString(), failure = failure.ToString(), nativeIdentityConfirmed = false, reasoningEffort }));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return item;
    }

    public static void Publish(IActivityCenterService? activity, NativeGatewayJournalEvent item, bool replay = false)
    {
        if (activity is null) return;
        try
        {
            var activityEvent = new ActivityEvent("native-gateway-journal:" + item.Id, item.At, ActivityEventKind.Execution,
                ActivityRoleNames.System, ActivityEvent.MapExecutionState(item.State), ActivityEventSource.Native,
                $"LLMGateway: локальный статус {item.State}",
                (item.Interrupted ? "Выполнение обнаружено после перезапуска. " : "") +
                $"Причина: {item.Failure}. Нативная идентичность ответа не подтверждена." +
                (item.State == ExecutionState.Ambiguous ? " Исход требует сверки; блокировки и лимиты сохранены." : ""),
                item.SessionId, item.ExecutionId);
            if (replay) activity.ReplayJournalEvent(activityEvent);
            else activity.Append(activityEvent);
        }
        catch (Exception) { /* Observers cannot change durable ownership. Replay uses the committed event. */ }
    }

    public static async Task<int> ReplayAsync(SqliteConnection connection, IActivityCenterService? activity, CancellationToken token)
    {
        if (activity is null) return 0;
        var events = new List<NativeGatewayJournalEvent>(); var invalidRows = 0;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ev.Id,e.Id,e.SessionId,ev.EventKind,ev.NormalizedRedactedPayloadJson,ev.OccurredAtUtc
            FROM ExecutionEvents ev JOIN Executions e ON e.Id=ev.ExecutionId JOIN Sessions s ON s.Id=e.SessionId
            WHERE s.Backend='NativeGateway' AND ev.EventKind IN ('NativeGatewayLifecycle','NativeGatewayInterrupted')
            ORDER BY ev.OccurredAtUtc DESC,e.Id DESC,ev.Sequence DESC,ev.Id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", activity.Capacity);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                try
                {
                    using var payload = JsonDocument.Parse(reader.GetString(4));
                    if (!Enum.TryParse<ExecutionState>(payload.RootElement.GetProperty("state").GetString(), out var state) || !Enum.IsDefined(state))
                        throw new InvalidDataException();
                    var interrupted = reader.GetString(3) == "NativeGatewayInterrupted";
                    var failure = ExecutionFailureReason.None;
                    // Older recovery rows did not have a failure field. Never display arbitrary stored text.
                    if (payload.RootElement.TryGetProperty("failure", out var value))
                    {
                        if (!Enum.TryParse(value.GetString(), out failure) || !Enum.IsDefined(failure)) throw new InvalidDataException();
                    }
                    else if (!interrupted) throw new InvalidDataException();
                    events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), state, failure,
                        interrupted, DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
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
                activity.ReplayJournalEvent(ActivityEvent.SystemEvent("native-gateway-journal:invalid-events",
                    "Некоторые события LLMGateway не удалось восстановить",
                    $"Повреждённых записей: {invalidRows}. Они сохранены в SQLite; остальные события загружены. Состояния выполнений и блокировки не изменены.",
                    DateTimeOffset.UtcNow));
            }
            catch (Exception) { /* A failing observer cannot abort startup. */ }
        }
        return events.Count;
    }
}

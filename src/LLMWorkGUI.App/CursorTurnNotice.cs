namespace LLMWorkGUI.App;

/// <summary>
/// Facts already known to the Cursor send path. This text does not claim a health transition
/// or a diagnostic bundle that this operation did not create.
/// </summary>
internal static class CursorTurnNotice
{
    public static string Uncertain(string routeId, string nativeModelId, string nativeSessionId) =>
        Describe("Исход Cursor не подтверждён", routeId, nativeModelId, nativeSessionId,
            "Требуется reconciliation.");

    public static string JournalCompletionFailed(string routeId, string nativeModelId, string nativeSessionId) =>
        Describe("Не удалось завершить запись Cursor", routeId, nativeModelId, nativeSessionId,
            "Требуется reconciliation перед повторной отправкой.");

    private static string Describe(string whatFailed, string routeId, string nativeModelId, string nativeSessionId, string nextAction) =>
        whatFailed + ". Маршрут " + routeId + " (Cursor, " + nativeModelId
        + "). Модель могла получить запрос. Нативная сессия " + nativeSessionId
        + " создана. Повтор небезопасен. Состояние здоровья не изменялось. " + nextAction
        + " Очищенные подробности отдельным diagnostic bundle не сохранялись.";
}

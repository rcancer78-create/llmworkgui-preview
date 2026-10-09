namespace LLMWorkGUI.App;

/// <summary>
/// Facts already known to the OpenCode send path. This text does not claim a health transition
/// or a diagnostic bundle that this operation did not create.
/// </summary>
internal static class OpenCodeTurnNotice
{
    public static string Uncertain(string routeId, string nativeModelId, string nativeSessionId) =>
        Describe("Исход OpenCode не подтверждён", routeId, nativeModelId, nativeSessionId,
            "Требуется reconciliation.");

    public static string JournalCompletionFailed(string routeId, string nativeModelId, string nativeSessionId) =>
        Describe("Не удалось завершить запись OpenCode", routeId, nativeModelId, nativeSessionId,
            "Требуется reconciliation перед повторной отправкой.");

    public static string PermissionReplyUncertain(string routeId, string nativeModelId, string nativeSessionId) =>
        "Ответ OpenCode не подтверждён. Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Ответ мог быть доставлен. Нативная сессия " + nativeSessionId
        + " создана. Повтор небезопасен. Состояние здоровья не изменялось. Требуется reconciliation. "
        + "Очищенные подробности отдельным diagnostic bundle не сохранялись.";

    public static string PermissionReplyNotSent(string routeId, string nativeModelId, string nativeSessionId) =>
        "Ответ OpenCode не отправлен. Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Нативная сессия " + nativeSessionId
        + " создана. Ответ не доставлялся. Состояние здоровья не изменялось.";

    public static string ResetUncertain(string routeId, string nativeModelId, string nativeSessionId) =>
        "Сброс OpenCode не подтверждён. Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Сброс мог быть доставлен. Нативная сессия " + nativeSessionId
        + " создана. Повтор небезопасен. Состояние здоровья не изменялось. Требуется reconciliation. "
        + "Очищенные подробности отдельным diagnostic bundle не сохранялись.";

    public static string ResetNotSent(string routeId, string nativeModelId, string nativeSessionId) =>
        "Сброс OpenCode не отправлен. Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Нативная сессия " + nativeSessionId
        + " создана. Сброс не доставлялся. Состояние здоровья не изменялось.";

    public static string PromptNotDelivered(string routeId, string nativeModelId) =>
        "Отправка OpenCode не начата. Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Запрос не доставлялся.";

    private static string Describe(string whatFailed, string routeId, string nativeModelId, string nativeSessionId, string nextAction) =>
        whatFailed + ". Маршрут " + routeId + " (OpenCode, " + nativeModelId
        + "). Модель могла получить запрос. Нативная сессия " + nativeSessionId
        + " создана. Повтор небезопасен. Состояние здоровья не изменялось. " + nextAction
        + " Очищенные подробности отдельным diagnostic bundle не сохранялись.";
}

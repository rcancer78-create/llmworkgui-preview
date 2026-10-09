namespace LLMWorkGUI.Application.Providers;

/// <summary>Stable catalog ID, not observed native response identity.</summary>
public static class GrokBotRestrictions
{
    public const string ProviderProfileId = "llmgateway-provider-29cb4ccf16783e54caa452607e5c29aac9c1a2f412f12cceac587bb8e97db1df";
    public const int MaxPromptCharacters = 64_000;
    public const int MaxPromptUtf8Bytes = 200_000;
    public const string ReviewInstructions = "Выполни текстовое ревью приведённого задания. Ответь замечаниями, с указанием серьёзности и ссылками на переданные материалы. Не изменяй файлы, не выполняй команды и не используй инструменты. Не утверждай, что прочитал материалы, которых нет в задании.\n\n";
    public const string Notice = "Интеграция Grok Bot отключена: чтение токена из хранилища другого приложения удалено. Для Grok используйте официальный клиент Cursor. Возобновление Grok Bot требует документированного публичного способа авторизации.";
}

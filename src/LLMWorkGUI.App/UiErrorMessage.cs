using System.IO;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App;

/// <summary>
/// Exceptions are untrusted diagnostic data, including validation and storage exceptions.
/// Never put their message, inner exception, Data or paths into a UI property. Structured
/// operation results and named workflow blockers remain the source of detailed user guidance.
/// </summary>
internal static class UiErrorMessage
{
    public static string Describe(Exception exception) => exception switch
    {
        OperationCanceledException => "Операция отменена.",
        TimeoutException => "Время ожидания истекло. Проверьте доступность сервиса и повторите попытку.",
        UnauthorizedAccessException => "Нет доступа к данным. Проверьте права доступа и повторите попытку.",
        IOException => "Не удалось прочитать или сохранить данные. Проверьте доступность хранилища.",
        LLMWorkGUI.Domain.Entities.ProjectLockConflictException => "Рабочий каталог занят другим выполнением. Дождитесь его завершения или проверьте восстановление сессий.",
        WorkflowValidationException { Failure: WorkflowValidationFailure.InvalidArchive } =>
            "Архив workflow повреждён или не является ZIP. Выберите корректный архив.",
        WorkflowValidationException { Failure: WorkflowValidationFailure.MissingModelResponse } =>
            "Нет сохранённого ответа модели (model response), связанного с хешем артефакта и проверенным вердиктом. Переход стадии запрещён.",
        WorkflowValidationException => "Проверка workflow не пройдена. Проверьте схему, назначения и состояние запуска.",
        ArgumentException or FormatException => "Некорректные параметры. Проверьте введённые значения.",
        NotSupportedException => "Эта операция не поддерживается текущей конфигурацией.",
        _ => "Операция не выполнена. Проверьте настройки и повторите попытку."
    };
}

using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.App.ViewModels;

internal static class ScreenCatalog
{
    public static IReadOnlyList<ScreenViewModel> CreateScreens(
        WorkspaceViewModel workspace,
        ProvidersAccountsViewModel providersAccounts,
        QuotasViewModel quotas,
        HealthCenterViewModel healthCenter,
        SettingsDiagnosticsViewModel settingsDiagnostics,
        WorkflowLibraryViewModel? workflowLibrary = null,
        IProjectRepository? projects = null, ISessionRepository? sessions = null,
        IExecutionRepository? executions = null, ISanitizedCatalogProvider? models = null,
        ModelsRoutesViewModel? modelsRoutes = null, IProjectDataPolicyService? projectDataPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(providersAccounts);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(healthCenter);
        ArgumentNullException.ThrowIfNull(settingsDiagnostics);

        return new ScreenViewModel[]
        {
            workspace,
            new CatalogScreenViewModel(
                ScreenId.Projects,
                "Проекты",
                "Ctrl+2",
                "Каталог проектов с ветками, настройками по умолчанию и блокировками.",
                "В каталоге пока нет проектов.",
                "Добавьте папку проекта. Новые проекты сохраняются с ограниченным доступом к данным.", projects, sessions, executions, models, projectDataPolicy),
            providersAccounts,
            (ScreenViewModel?)modelsRoutes ?? new CatalogScreenViewModel(
                ScreenId.Models,
                "Модели",
                "Ctrl+4",
                "Возможности моделей, рассуждение/скорость и доступность маршрутов.",
                "Модели пока не обнаружены.",
                "Модели из настроенных аккаунтов. Доступность маршрутов проверяется перед отправкой.", projects, sessions, executions, models),
            quotas,
            new CatalogScreenViewModel(
                ScreenId.Sessions,
                "Сессии",
                "Ctrl+6",
                "Привязки сессий, нативные ID, продолжение/форк/сброс.",
                "Сессии пока не записаны.",
                "Сохранённые сессии всех проектов. Управление активной сессией доступно в рабочей области.", projects, sessions, executions, models),
            new CatalogScreenViewModel(
                ScreenId.Runs,
                "Запуски",
                "Ctrl+7",
                "Активные и завершённые выполнения.",
                "Выполнения пока не записаны.",
                "История выполнений всех проектов. Для актуального состояния нажмите «Обновить».", projects, sessions, executions, models),
            (ScreenViewModel?)workflowLibrary ?? new CatalogScreenViewModel(
                ScreenId.Workflows,
                "Процессы",
                "Ctrl+8",
                "Импорт процессов, версии, diff, привязка и адаптация.",
                "Процессы пока не импортированы.",
                "Библиотека процессов, версионирование и адаптация моделей появятся на этапе оркестрации процессов."),
            healthCenter,
            settingsDiagnostics
        };
    }
}

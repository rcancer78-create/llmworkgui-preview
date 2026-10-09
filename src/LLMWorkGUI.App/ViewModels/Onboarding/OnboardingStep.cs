namespace LLMWorkGUI.App.ViewModels.Onboarding;

/// <summary>The four first-run steps of the frictionless onboarding flow (ROADMAP Phase 11).</summary>
public enum OnboardingStepKind
{
    WelcomeAndWorkspace,
    LocalCliDetection,
    WorkflowCatalogDiscovery,
    ReadySafeMode
}

/// <summary>
/// One immutable step of the onboarding wizard. The step only carries presentation text and its action
/// hint; it never triggers a model call, a network request or a credential prompt.
/// </summary>
public sealed class OnboardingStep
{
    private static readonly OnboardingStep[] Steps =
    {
        new(
            OnboardingStepKind.WelcomeAndWorkspace,
            1,
            "Приветствие и рабочая область",
            "Выберите или подтвердите рабочий каталог проекта. Он не изменяется и не загружается наверх.",
            "Открыть или подтвердить локальный каталог проекта."),
        new(
            OnboardingStepKind.LocalCliDetection,
            2,
            "Обнаружение локальных CLI и бэкендов",
            "OpenCode и Cursor Agent обнаруживаются локально; star-cliproxy и Mirasim показаны как адаптеры бэкендов "
            + "с честным статусом. Неподтверждённый источник отображается как Not reported, отсутствие CLI — деградированный режим.",
            "Проверить локальные исполняемые файлы на PATH."),
        new(
            OnboardingStepKind.WorkflowCatalogDiscovery,
            3,
            "Каталог шаблонов сценариев и матрица ролей",
            "Готовые версионированные шаблоны, шаблоны документов и ролевые привязки доступны без импорта.",
            "Просмотреть встроенные шаблоны и матрицу ролей."),
        new(
            OnboardingStepKind.ReadySafeMode,
            4,
            "Готовность и безопасная симуляция",
            "Можно открыть рабочую область или запустить локальную synthetic-симуляцию без реальных вызовов моделей.",
            "Открыть рабочую область или включить безопасную симуляцию.")
    };

    private OnboardingStep(
        OnboardingStepKind kind,
        int ordinal,
        string title,
        string description,
        string actionHint)
    {
        Kind = kind;
        Ordinal = ordinal;
        Title = title;
        Description = description;
        ActionHint = actionHint;
    }

    /// <summary>All steps in wizard order. The list is immutable and shared.</summary>
    public static IReadOnlyList<OnboardingStep> All => Steps;

    public OnboardingStepKind Kind { get; }

    public int Ordinal { get; }

    public string Title { get; }

    public string Description { get; }

    public string ActionHint { get; }

    public string OrdinalDisplay => $"{Ordinal}/{Steps.Length}";

    public static OnboardingStep FromKind(OnboardingStepKind kind) =>
        Steps.First(step => step.Kind == kind);
}

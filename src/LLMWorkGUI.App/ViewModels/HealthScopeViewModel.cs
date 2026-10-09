using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;


namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Display projection of one observed health scope. Every label comes from the snapshot: the view
/// never derives "healthy" or "recovered" on its own.
/// </summary>
public sealed class HealthScopeViewModel
{
    public HealthScopeViewModel(HealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Snapshot = snapshot;
    }

    public HealthSnapshot Snapshot { get; }

    public HealthScope Scope => Snapshot.Scope;

    /// <summary>Stable identity used to keep the operator's selection across a refresh.</summary>
    public string Key => $"{Scope.ScopeType}:{Scope.ScopeId}";

    public string ScopeTypeDisplay => Scope.ScopeType == HealthScope.ModelRouteScopeType ? "Модель аккаунта" : Scope.ScopeType;

    public string ScopeIdDisplay => Scope.TryGetModelRoute(out var account, out var model) ? $"{account} / {model}" : Scope.ScopeId;

    public HealthState State => Snapshot.State;

    public string StateDisplay => Snapshot.AuthenticationFanoutPending
        ? "Заблокировано: обработка ошибки авторизации не завершена. Нажмите «Обновить» для повтора."
        : Snapshot.State.ToString();

    /// <summary>Error class of the counted failures, or <c>Not reported</c> when none is known.</summary>
    public string ErrorClassDisplay =>
        Snapshot.ErrorClass?.ToString() ?? HealthCenterViewModel.UnavailableIndicator;

    public string FailureCountDisplay => Snapshot.AccountedFailureCount.ToString();

    public string CooldownDisplay =>
        Snapshot.CooldownUntil is { } until
            ? until.ToString("u")
            : HealthCenterViewModel.UnavailableIndicator;

    public bool IsRoutable => Snapshot.IsRoutable;

    public bool RequiresProbe => Snapshot.RequiresProbe;

    public bool IsForcedWithoutVerification => Snapshot.IsForcedWithoutVerification;

    /// <summary>Whether the circuit breaker currently keeps this scope out of routing.</summary>
    public string RoutingDisplay => Snapshot.IsRoutable ? "В маршрутизации" : "Исключён из маршрутизации";

    /// <summary>
    /// Warning shown for a route that is only usable because an operator forced it. It must never read
    /// like a verified recovery.
    /// </summary>
    public string ForcedWarning =>
        "Принудительно включён в маршрутизацию без успешной проверки; восстановление не подтверждено.";

    /// <summary>Warning shown while the scope still owes a probe before it may recover.</summary>
    public string ProbeWarning => "Для восстановления требуется успешное прохождение проверки (ТЗ §6.10).";
}

/// <summary>
/// Display projection of one session affected by an unhealthy route. Every label comes from the
/// classified impact: the view never decides on its own what the operator must do.
/// </summary>
public sealed class ImpactedSessionViewModel
{
    public ImpactedSessionViewModel(ImpactedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
    }

    public ImpactedSession Session { get; }

    public string SessionIdDisplay => Session.SessionId;

    public string ProjectIdDisplay => Session.ProjectId;

    public string StateDisplay => Session.State.ToString();

    /// <summary>Native backend id, or <c>Not reported</c> when the backend never confirmed one.</summary>
    public string NativeSessionIdDisplay =>
        Session.NativeSessionId ?? HealthCenterViewModel.UnavailableIndicator;

    public string RoleDisplay => Session.Role ?? HealthCenterViewModel.UnavailableIndicator;

    public string LastEventAtDisplay => Session.LastEventAt.ToString("u");

    /// <summary>Classified impact, shown verbatim so the UI cannot soften it.</summary>
    public string ImpactDisplay => Session.Impact switch
    {
        SessionImpact.RunningTurnRequiresStop => "Выполняемый ход должен быть остановлен — замена сессии требует вашего подтверждения",
        SessionImpact.NextTurnBlocked => "Следующий ход заблокирован",
        SessionImpact.AwaitingReconciliation => "Ожидает сверки",
        SessionImpact.ProceedingOnUnverifiedRoute => "Продолжение по непроверенному маршруту",
        SessionImpact.None => "Не затронуто",
        _ => HealthCenterViewModel.UnavailableIndicator
    };

    public string ExplanationDisplay => Session.Explanation;

    public bool RequiresReplacementSession => Session.RequiresReplacementSession;

    public bool HasTurnInFlight => Session.HasTurnInFlight;
}

/// <summary>Display projection of one audited health transition.</summary>
public sealed class HealthAuditEntryViewModel
{
    public HealthAuditEntryViewModel(HealthEventView entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
    }

    public HealthEventView Entry { get; }

    public string OccurredAtDisplay => Entry.OccurredAt.ToString("u");

    /// <summary>Previous state, or <c>Not reported</c> for the first observation of a scope.</summary>
    public string PreviousStateDisplay =>
        Entry.PreviousState?.ToString() ?? HealthCenterViewModel.UnavailableIndicator;

    public string NewStateDisplay => Entry.NewState.ToString();

    public string ErrorClassDisplay =>
        Entry.ErrorClass?.ToString() ?? HealthCenterViewModel.UnavailableIndicator;

    public string ReasonDisplay =>
        string.IsNullOrWhiteSpace(Entry.Reason)
            ? HealthCenterViewModel.UnavailableIndicator
            : Entry.Reason!;

    public bool IsForcedRecovery => Entry.IsForcedRecovery;

    public bool IsVerifiedRecovery => Entry.IsVerifiedRecovery;

    /// <summary>
    /// How this transition is classified for the operator. Forced and verified recoveries are labelled
    /// differently on purpose, so the audit cannot blur them together.
    /// </summary>
    public string RecoveryKindDisplay => Entry switch
    {
        { IsVerifiedRecovery: true } => "Подтверждённое восстановление",
        { IsForcedRecovery: true } => "Принудительно, без проверки",
        _ => "Переход"
    };
}

using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Observability;

public sealed class ObservableRunProjection
{
    public const string NotReportedPlaceholder = "Not reported";

    public ObservableRunProjection(
        string executionId,
        string sessionId,
        WorkflowRole role,
        string displayLabel,
        ExecutionState state,
        string requestedRouteId,
        string? observedRouteId,
        string? nativeSessionId,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset lastActivityAtUtc,
        DateTimeOffset? endedAtUtc,
        EvidenceSourceKind evidenceSource,
        bool isSynthetic,
        string? observedModelId = null,
        string? observedAccountId = null,
        string? observedExecutionMode = null,
        bool isReadOnlyTurn = false)
    {
        ExecutionId = ApplicationGuard.NotBlank(executionId, nameof(executionId));
        SessionId = ApplicationGuard.NotBlank(sessionId, nameof(sessionId));
        Role = role;
        DisplayLabel = ApplicationGuard.NotBlank(displayLabel, nameof(displayLabel));
        State = state;
        RequestedRouteId = ApplicationGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = ApplicationGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        StartedAtUtc = startedAtUtc;
        LastActivityAtUtc = lastActivityAtUtc;
        EndedAtUtc = endedAtUtc;
        EvidenceSource = evidenceSource;
        IsSynthetic = isSynthetic;

        ObservedModelId = ApplicationGuard.OptionalNotBlank(observedModelId, nameof(observedModelId));
        ObservedAccountId = ApplicationGuard.OptionalNotBlank(observedAccountId, nameof(observedAccountId));
        ObservedExecutionMode = ApplicationGuard.OptionalNotBlank(observedExecutionMode, nameof(observedExecutionMode));
        IsReadOnlyTurn = isReadOnlyTurn;

        EnsureSyntheticEvidenceInvariant(evidenceSource, isSynthetic);
        EnsureChronology(startedAtUtc, lastActivityAtUtc, endedAtUtc);
    }

    public string ExecutionId { get; }

    public string SessionId { get; }

    public WorkflowRole Role { get; }

    public string DisplayLabel { get; }

    public ExecutionState State { get; }

    public string RequestedRouteId { get; }

    public string? ObservedRouteId { get; }

    public string? NativeSessionId { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public DateTimeOffset LastActivityAtUtc { get; }

    public DateTimeOffset? EndedAtUtc { get; }

    public EvidenceSourceKind EvidenceSource { get; }

    public bool IsSynthetic { get; }

    /// <summary>
    /// The model actually observed on the session binding of this turn. Null when the domain did not
    /// report it, and never derived from the requested route or from a provider catalog (ТЗ §7.4).
    /// </summary>
    public string? ObservedModelId { get; }

    /// <summary>The account actually observed on the session binding of this turn, or null.</summary>
    public string? ObservedAccountId { get; }

    /// <summary>
    /// The execution mode actually observed on the session binding of this turn, or null. A null mode is
    /// a writer turn, so it can never prove a read-only turn.
    /// </summary>
    public string? ObservedExecutionMode { get; }

    /// <summary>
    /// True only when <see cref="ICheckoutLockService.RequiresWriterLock"/> was applied to the observed
    /// execution mode and proved that this turn does not take the writer lock. Two writer turns never
    /// count as parallel read-only activity.
    /// </summary>
    public bool IsReadOnlyTurn { get; }

    public bool HasObservedModel => ObservedModelId is not null;

    public bool HasObservedAccount => ObservedAccountId is not null;

    public string ObservedRouteIdDisplay => ObservedRouteId ?? NotReportedPlaceholder;

    public string NativeSessionIdDisplay => NativeSessionId ?? NotReportedPlaceholder;

    public string ObservedModelIdDisplay => ObservedModelId ?? NotReportedPlaceholder;

    public string ObservedAccountIdDisplay => ObservedAccountId ?? NotReportedPlaceholder;

    public string ObservedExecutionModeDisplay => ObservedExecutionMode ?? NotReportedPlaceholder;

    public bool IsActive => !ExecutionStateMachine.IsTerminal(State);

    public static ObservableRunProjection FromExecution(
        Execution execution,
        Session session,
        WorkflowRole role,
        string? displayLabel = null,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported,
        DateTimeOffset? lastActivityAtUtc = null,
        ICheckoutLockService? checkoutLockService = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(session);

        if (!string.Equals(execution.SessionId, session.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The execution does not belong to the supplied session.", nameof(session));
        }

        // Model, account and the read-only proof come from the session binding the execution actually ran
        // on. Without the lock policy the read-only claim is not proven, so it stays false.
        var isReadOnlyTurn = checkoutLockService is not null
            && !checkoutLockService.RequiresWriterLock(session.Binding.ExecutionMode);

        return new ObservableRunProjection(
            execution.Id,
            session.Id,
            role,
            string.IsNullOrWhiteSpace(displayLabel) ? role.ToString() : displayLabel,
            execution.State,
            execution.RequestedRouteId,
            execution.ObservedRouteId,
            session.NativeSessionId,
            execution.StartedAt,
            lastActivityAtUtc ?? ResolveLastActivityAtUtc(execution, session),
            execution.EndedAt,
            evidenceSource,
            evidenceSource == EvidenceSourceKind.SyntheticFixture,
            session.Binding.ModelId,
            session.Binding.AccountId,
            session.Binding.ExecutionMode,
            isReadOnlyTurn);
    }

    private static DateTimeOffset ResolveLastActivityAtUtc(Execution execution, Session session)
    {
        var candidate = execution.EndedAt ?? execution.StartedAt ?? execution.CreatedAt;

        var executionIsActive = string.Equals(session.ActiveExecutionId, execution.Id, StringComparison.Ordinal);

        if (execution.EndedAt is null && executionIsActive && session.LastEventAt > candidate)
        {
            candidate = session.LastEventAt;
        }

        return candidate;
    }

    private static void EnsureSyntheticEvidenceInvariant(EvidenceSourceKind evidenceSource, bool isSynthetic)
    {
        if (isSynthetic && evidenceSource != EvidenceSourceKind.SyntheticFixture)
        {
            throw new ArgumentException(
                "Synthetic runs must declare EvidenceSourceKind.SyntheticFixture and can never be reported as native protocol evidence.",
                nameof(evidenceSource));
        }

        if (!isSynthetic && evidenceSource == EvidenceSourceKind.SyntheticFixture)
        {
            throw new ArgumentException(
                "EvidenceSourceKind.SyntheticFixture requires IsSynthetic to be true.",
                nameof(isSynthetic));
        }
    }

    private static void EnsureChronology(
        DateTimeOffset? startedAtUtc,
        DateTimeOffset lastActivityAtUtc,
        DateTimeOffset? endedAtUtc)
    {
        if (startedAtUtc is not { } startedAt)
        {
            return;
        }

        if (endedAtUtc is { } endedAt && endedAt < startedAt)
        {
            throw new ArgumentException("EndedAtUtc cannot be earlier than StartedAtUtc.", nameof(endedAtUtc));
        }

        if (lastActivityAtUtc < startedAt)
        {
            throw new ArgumentException("LastActivityAtUtc cannot be earlier than StartedAtUtc.", nameof(lastActivityAtUtc));
        }
    }
}

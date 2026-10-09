using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Observability;

public sealed class SyntheticRunSequenceFixture
{
    public const string SyntheticProjectId = "synthetic-project";
    public const string SyntheticWorkspaceRoot = "C:\\synthetic\\workspace";
    public const string SyntheticWorkflowRunId = "synthetic-workflow-run-1";
    public const string CanonicalSequenceDescription = "Coordinator → Executor → Reviewer → Fix → Acceptance";

    public static readonly DateTimeOffset DefaultBaseTimestampUtc =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<string> CanonicalStageOrder { get; } = new[]
    {
        "Coordinator",
        "Executor",
        "Reviewer",
        "Fix",
        "Acceptance"
    };

    private static readonly SessionBinding SyntheticBinding = new SessionBinding(
        BackendType.OpenCode,
        "synthetic-provider-profile",
        "synthetic-account",
        "synthetic-model",
        "high",
        "fast",
        "interactive");

    private readonly List<Session> _sessions = new();

    public SyntheticRunSequenceFixture()
        : this(DefaultBaseTimestampUtc)
    {
    }

    public SyntheticRunSequenceFixture(DateTimeOffset baseTimestampUtc)
    {
        BaseTimestampUtc = baseTimestampUtc;

        Canonical = BuildCanonicalSequence(baseTimestampUtc);
        RouteMismatch = BuildRouteMismatch(baseTimestampUtc.AddHours(1));
        Ambiguous = BuildAmbiguous(baseTimestampUtc.AddHours(2));
        WaitingApprovalPending = BuildWaitingApproval(baseTimestampUtc.AddHours(3), ApprovalOutcome.Pending);
        WaitingApprovalApproved = BuildWaitingApproval(baseTimestampUtc.AddHours(4), ApprovalOutcome.Approved);
        WaitingApprovalDenied = BuildWaitingApproval(baseTimestampUtc.AddHours(5), ApprovalOutcome.Denied);

        AllScenarios = new[]
        {
            Canonical,
            RouteMismatch,
            Ambiguous,
            WaitingApprovalPending,
            WaitingApprovalApproved,
            WaitingApprovalDenied
        };

        AllSteps = AllScenarios.SelectMany(scenario => scenario.Steps).ToArray();
        AllSessions = _sessions.ToArray();
        AllExecutions = AllSteps.Select(step => step.Execution).ToArray();
        AllProjections = AllSteps.Select(step => step.Projection).ToArray();
    }

    public DateTimeOffset BaseTimestampUtc { get; }

    public bool IsSynthetic => true;

    public SyntheticScenario Canonical { get; }

    public SyntheticScenario RouteMismatch { get; }

    public SyntheticScenario Ambiguous { get; }

    public SyntheticScenario WaitingApprovalPending { get; }

    public SyntheticScenario WaitingApprovalApproved { get; }

    public SyntheticScenario WaitingApprovalDenied { get; }

    public IReadOnlyList<SyntheticScenario> AllScenarios { get; }

    public IReadOnlyList<SyntheticRunStep> AllSteps { get; }

    public IReadOnlyList<Session> AllSessions { get; }

    public IReadOnlyList<Execution> AllExecutions { get; }

    public IReadOnlyList<ObservableRunProjection> AllProjections { get; }

    private SyntheticScenario BuildCanonicalSequence(DateTimeOffset baseTimestampUtc)
    {
        var plans = new[]
        {
            new RunPlan("Coordinator", WorkflowRole.Coordinator, 1, "synthetic-route-coordinator"),
            new RunPlan("Executor", WorkflowRole.Executor, 2, "synthetic-route-executor"),
            new RunPlan("Reviewer", WorkflowRole.Reviewer, 3, "synthetic-route-reviewer"),
            new RunPlan("Fix", WorkflowRole.Executor, 4, "synthetic-route-fix"),
            new RunPlan("Acceptance", WorkflowRole.Coordinator, 5, "synthetic-route-acceptance")
        };

        var steps = new SyntheticRunStep[plans.Length];

        for (var index = 0; index < plans.Length; index++)
        {
            var plan = plans[index];
            var startedAt = baseTimestampUtc.AddMinutes(index * 10);
            var endedAt = startedAt.AddMinutes(5);
            var nativeSessionId = $"synthetic-native-session-{plan.Sequence}";

            var machine = new ExecutionStateMachine();
            machine.Start();
            machine.ConfirmSession(nativeSessionId, SyntheticBinding);
            machine.MarkRunning();
            machine.Succeed();

            var session = CreateSession(
                $"synthetic-session-{plan.Sequence}",
                nativeSessionId,
                SessionState.Idle,
                ReconciliationOutcome.Reattached,
                plan.Role.ToString(),
                activeExecutionId: null,
                createdAt: startedAt.AddMinutes(-2),
                lastEventAt: endedAt);

            var execution = CreateExecution(
                $"synthetic-execution-{plan.Sequence}",
                session,
                machine,
                plan.RequestedRouteId,
                plan.RequestedRouteId,
                terminationReason: null,
                createdAt: startedAt.AddMinutes(-1),
                startedAt: startedAt,
                endedAt: endedAt);

            steps[index] = CreateStep(plan.StageLabel, plan.Role, session, execution);
        }

        return new SyntheticScenario(CanonicalSequenceDescription, steps);
    }

    private SyntheticScenario BuildRouteMismatch(DateTimeOffset startedAt)
    {
        const string nativeSessionId = "synthetic-native-session-mismatch";
        var endedAt = startedAt.AddMinutes(3);

        var machine = new ExecutionStateMachine();
        machine.Start();
        machine.ConfirmSession(nativeSessionId, SyntheticBinding);
        machine.MarkRunning();
        machine.MarkRouteMismatch();

        var session = CreateSession(
            "synthetic-session-mismatch",
            nativeSessionId,
            SessionState.Ambiguous,
            ReconciliationOutcome.Ambiguous,
            WorkflowRole.Executor.ToString(),
            activeExecutionId: null,
            createdAt: startedAt.AddMinutes(-2),
            lastEventAt: endedAt);

        var execution = CreateExecution(
            "synthetic-execution-mismatch",
            session,
            machine,
            requestedRouteId: "synthetic-route-requested",
            observedRouteId: "synthetic-route-observed",
            terminationReason: "observed route does not match the requested route",
            createdAt: startedAt.AddMinutes(-1),
            startedAt: startedAt,
            endedAt: endedAt);

        var step = CreateStep("Executor", WorkflowRole.Executor, session, execution);

        return new SyntheticScenario("RouteMismatch", new[] { step });
    }

    private SyntheticScenario BuildAmbiguous(DateTimeOffset startedAt)
    {
        var endedAt = startedAt.AddMinutes(4);

        var machine = new ExecutionStateMachine();
        machine.Start();
        machine.MarkAmbiguous();

        var session = CreateSession(
            "synthetic-session-ambiguous",
            nativeSessionId: null,
            SessionState.Ambiguous,
            ReconciliationOutcome.Ambiguous,
            WorkflowRole.Executor.ToString(),
            activeExecutionId: null,
            createdAt: startedAt.AddMinutes(-2),
            lastEventAt: endedAt);

        var execution = CreateExecution(
            "synthetic-execution-ambiguous",
            session,
            machine,
            requestedRouteId: "synthetic-route-requested",
            observedRouteId: null,
            terminationReason: "terminal evidence was never reported",
            createdAt: startedAt.AddMinutes(-1),
            startedAt: startedAt,
            endedAt: endedAt);

        var step = CreateStep("Executor", WorkflowRole.Executor, session, execution);

        return new SyntheticScenario("Ambiguous", new[] { step });
    }

    private SyntheticScenario BuildWaitingApproval(DateTimeOffset startedAt, ApprovalOutcome outcome)
    {
        var suffix = outcome switch
        {
            ApprovalOutcome.Pending => "pending",
            ApprovalOutcome.Approved => "approved",
            ApprovalOutcome.Denied => "denied",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        var sessionId = $"synthetic-session-approval-{suffix}";
        var executionId = $"synthetic-execution-approval-{suffix}";
        var nativeSessionId = $"synthetic-native-session-approval-{suffix}";

        var machine = new ExecutionStateMachine();
        machine.Start();
        machine.ConfirmSession(nativeSessionId, SyntheticBinding);
        machine.MarkRunning();
        machine.RequestApproval();

        DateTimeOffset? endedAt = null;
        string? terminationReason = null;
        var sessionState = SessionState.Active;
        string? activeExecutionId = executionId;

        switch (outcome)
        {
            case ApprovalOutcome.Pending:
                break;
            case ApprovalOutcome.Approved:
                machine.ResolveApproval();
                machine.Succeed();
                endedAt = startedAt.AddMinutes(4);
                sessionState = SessionState.Idle;
                activeExecutionId = null;
                break;
            case ApprovalOutcome.Denied:
                machine.RequestCancel();
                machine.ConfirmCancelled();
                endedAt = startedAt.AddMinutes(4);
                terminationReason = "approval denied by the operator";
                sessionState = SessionState.Idle;
                activeExecutionId = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var session = CreateSession(
            sessionId,
            nativeSessionId,
            sessionState,
            ReconciliationOutcome.Reattached,
            WorkflowRole.Executor.ToString(),
            activeExecutionId,
            createdAt: startedAt.AddMinutes(-2),
            lastEventAt: endedAt ?? startedAt.AddMinutes(1));

        var execution = CreateExecution(
            executionId,
            session,
            machine,
            requestedRouteId: "synthetic-route-executor",
            observedRouteId: "synthetic-route-executor",
            terminationReason,
            createdAt: startedAt.AddMinutes(-1),
            startedAt: startedAt,
            endedAt: endedAt);

        var step = CreateStep("Executor", WorkflowRole.Executor, session, execution);

        return new SyntheticScenario($"WaitingApproval{outcome}", new[] { step });
    }

    private SyntheticRunStep CreateStep(
        string stageLabel,
        WorkflowRole role,
        Session session,
        Execution execution)
    {
        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            role,
            stageLabel,
            EvidenceSourceKind.SyntheticFixture);

        return new SyntheticRunStep(stageLabel, role, session, execution, projection);
    }

    private Session CreateSession(
        string sessionId,
        string? nativeSessionId,
        SessionState state,
        ReconciliationOutcome reconciliationOutcome,
        string? roleLabel,
        string? activeExecutionId,
        DateTimeOffset createdAt,
        DateTimeOffset lastEventAt)
    {
        var session = new Session(
            sessionId,
            SyntheticBinding,
            SyntheticProjectId,
            SyntheticWorkspaceRoot,
            nativeSessionId,
            state,
            reconciliationOutcome,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: SyntheticWorkflowRunId,
            role: roleLabel,
            activeExecutionId: activeExecutionId,
            createdAt,
            lastEventAt);

        _sessions.Add(session);
        return session;
    }

    private static Execution CreateExecution(
        string executionId,
        Session session,
        ExecutionStateMachine machine,
        string requestedRouteId,
        string? observedRouteId,
        string? terminationReason,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt)
    {
        return new Execution(
            executionId,
            session.Id,
            $"synthetic-client-request-{executionId}",
            machine.State,
            machine.FailureReason,
            requestedRouteId,
            observedRouteId,
            retryOfExecutionId: null,
            processState: null,
            exitCode: null,
            terminationReason,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            createdAt,
            startedAt,
            endedAt);
    }

    private sealed record RunPlan(string StageLabel, WorkflowRole Role, int Sequence, string RequestedRouteId);

    private enum ApprovalOutcome
    {
        Pending,
        Approved,
        Denied
    }
}

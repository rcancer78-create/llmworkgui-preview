using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Tests;

internal static class RunTestData
{
    public static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    public static SessionBinding Binding { get; } = CreateBinding();

    public static SessionBinding CreateBinding(
        string accountId = "account-1",
        string modelId = "model-1",
        string? executionMode = "interactive") =>
        new SessionBinding(
            BackendType.OpenCode,
            "provider-1",
            accountId,
            modelId,
            "high",
            "fast",
            executionMode);

    public static Session CreateSession(
        string id = "session-1",
        string projectId = "project-1",
        string? role = "Executor",
        string? nativeSessionId = "native-session-1",
        SessionState state = SessionState.Idle,
        ReconciliationOutcome reconciliationOutcome = ReconciliationOutcome.Reattached,
        string? activeExecutionId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastEventAt = null,
        SessionBinding? binding = null)
    {
        var created = createdAt ?? BaseTime;

        return new Session(
            id,
            binding ?? Binding,
            projectId,
            "C:\\test\\workspace",
            nativeSessionId,
            state,
            reconciliationOutcome,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: "workflow-run-1",
            role,
            activeExecutionId,
            created,
            lastEventAt ?? created.AddMinutes(5));
    }

    public static Execution CreateExecution(
        string id = "execution-1",
        string sessionId = "session-1",
        ExecutionState state = ExecutionState.Succeeded,
        string requestedRouteId = "route-requested",
        string? observedRouteId = "route-observed",
        ExecutionFailureReason failureReason = ExecutionFailureReason.None,
        string? terminationReason = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        var created = createdAt ?? BaseTime;

        return new Execution(
            id,
            sessionId,
            $"client-request-{id}",
            state,
            failureReason,
            requestedRouteId,
            observedRouteId,
            retryOfExecutionId: null,
            processState: null,
            exitCode: null,
            terminationReason,
            artifacts: Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            created,
            startedAt,
            endedAt);
    }
}

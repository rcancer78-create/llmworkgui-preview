using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

/// <summary>One local turn. Native session continuation is not supported by this gateway contract.</summary>
public sealed record NativeGatewayTurnRequest(string ProjectId, string RootPath, string RouteId,
    string ClientRequestId, string Prompt)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public NativeGatewayRouteBinding? ExpectedBinding { get; init; }
    /// <summary>Opaque authority-owned preview ID. A caller-owned payload or boolean is not consent.</summary>
    public Guid? EgressPreviewId { get; init; }
}

public sealed record NativeGatewayTurnResult(string SessionId, string ExecutionId, ExecutionState State,
    ExecutionFailureReason FailureReason, string? Content, bool RequiresReconciliation);

public interface INativeGatewayTurnService
{
    Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken cancellationToken = default);
}

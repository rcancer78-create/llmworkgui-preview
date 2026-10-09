namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Request to start one managed <c>cursor-agent acp</c> process instance.</summary>
public sealed record CursorAcpProcessStartRequest
{
    /// <summary>Execution identity used for the run/spool directory outside any project checkout.</summary>
    public required string ExecutionId { get; init; }
}

using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record TurnResult
{
    public const string CompletedStatus = "Completed";

    public const string CancelledStatus = "Cancelled";

    public const string FailedStatus = "Failed";

    public required string SessionId { get; init; }

    public required string OutputText { get; init; }

    public required string Status { get; init; }

    public string? FinishReason { get; init; }

    public OpenCodeTokenUsage? Tokens { get; init; }

    public IReadOnlyList<ToolCallInfo> ToolCalls { get; init; } = Array.Empty<ToolCallInfo>();

    public string? ErrorMessage { get; init; }

    /// <summary>A prompt may still be running; account admission and checkout ownership must be retained.</summary>
    public bool IsDeliveryUncertain { get; init; }
    /// <summary>The supervisor budget expired; independent of whether prompt delivery was possible.</summary>
    public bool WasTimedOut { get; init; }

    /// <summary>Model id reported by the native assistant message. Null when that event did not report one.</summary>
    public string? ObservedModelId { get; init; }
    /// <summary>Provider reported alongside the native model; never copied from the requested route.</summary>
    public string? ObservedProviderId { get; init; }
}

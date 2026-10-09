namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed record MirasimTurnResult
{
    public required string TurnId { get; init; }

    public required MirasimTurnStatus Status { get; init; }

    public MirasimTurnErrorClass ErrorClass { get; init; }

    public string? ErrorMessage { get; init; }

    public string? AssistantResponse { get; init; }

    public string? ObservedModel { get; init; }

    public string? ObservedAccount { get; init; }

    public string? ObservedLeg { get; init; }

    public string RouteMode { get; init; } = MirasimRouteModes.ManualOnly;

    /// <summary>Only a pre-transport refusal can request local cleanup; no remote operation is retried.</summary>
    public bool RequiresLocalCleanup { get; init; }

    public bool IsTerminal =>
        !RequiresLocalCleanup && (Status is MirasimTurnStatus.Completed
            or MirasimTurnStatus.Failed
            or MirasimTurnStatus.Cancelled
            or MirasimTurnStatus.RefusedByLock
            or MirasimTurnStatus.RefusedByPolicy
            or MirasimTurnStatus.Incomplete
            or MirasimTurnStatus.UnsupportedTransport);

    public bool IsAmbiguous => Status == MirasimTurnStatus.Ambiguous;
}

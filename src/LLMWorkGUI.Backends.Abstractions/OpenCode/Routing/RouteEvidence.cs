using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;

public sealed record RouteEvidence
{
    public required SessionBinding RequestedBinding { get; init; }

    public SessionBinding? ObservedBinding { get; init; }

    public string? EvidenceSource { get; init; }

    public required DateTime CheckedAtUtc { get; init; }

    public required bool IsMatched { get; init; }

    public RouteVerificationResult Result { get; init; } = RouteVerificationResult.Mismatch;

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
}

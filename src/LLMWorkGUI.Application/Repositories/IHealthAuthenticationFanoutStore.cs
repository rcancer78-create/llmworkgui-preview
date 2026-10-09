using LLMWorkGUI.Application.Health;

namespace LLMWorkGUI.Application.Repositories;

public sealed record HealthAuthenticationProjection(
    string Id, HealthScope Scope, string? AccountId, string Reason, DateTimeOffset ObservedAt)
{
    public IReadOnlyList<string> ProtectedAccountIds { get; init; } = [];
    public string? EvidenceRedactedJson { get; init; }
}

/// <summary>Durable admission barriers, retired atomically with each projected state and audit.</summary>
public interface IHealthAuthenticationFanoutStore
{
    Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken cancellationToken);
    Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken cancellationToken);
    Task<bool> HasPendingAsync(HealthScope scope, CancellationToken cancellationToken);
    /// <summary>Reads state and pending barrier from one database snapshot.</summary>
    Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken cancellationToken);
    Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent, string projectionId,
        CancellationToken cancellationToken);
}

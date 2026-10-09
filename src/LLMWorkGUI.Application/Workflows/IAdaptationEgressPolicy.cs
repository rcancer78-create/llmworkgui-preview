namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationPolicySnapshot(string Fingerprint, string RootPath);

/// <summary>Current SQL material/project/route preflight; not HTTP authority or native terminal proof.</summary>
public interface IAdaptationEgressPolicy
{
    /// <summary>Prepares exact service-built text against current stored provenance; returned ID is consumed once.</summary>
    Task<Guid> PrepareAsync(AdaptationModelRequest request, CancellationToken cancellationToken);
    Task<AdaptationPolicySnapshot> ValidateAsync(AdaptationModelRequest request, AdaptationRouteIdentity identity,
        string exactPrompt, string? expectedFingerprint, CancellationToken cancellationToken);
}

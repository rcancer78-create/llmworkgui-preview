using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

/// <summary>A scoped capability declaration, not proof of credentials or response origin.
/// Publishers must supply their actual source and an explicit validity interval.</summary>
public sealed record ModelCapabilityEvidence(
    string ModelId,
    string AccountId,
    CapabilityState State,
    ModelProvenance Provenance,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ModelCapabilityFlags Flags,
    IReadOnlyList<string> ReasoningEfforts,
    IReadOnlyList<string> SpeedModes,
    IReadOnlyList<string> ExecutionModes,
    int? ContextLimit = null)
{
    public string? DiscoverySource { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(ModelId) && ModelId.Length <= 256 && !ModelId.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(AccountId) && AccountId.Length <= 256 && !AccountId.Any(char.IsControl)
        && Enum.IsDefined(State) && Enum.IsDefined(Provenance)
        && ObservedAtUtc != default && ExpiresAtUtc > ObservedAtUtc
        && (Flags & ~(ModelCapabilityFlags.Chat | ModelCapabilityFlags.Vision
            | ModelCapabilityFlags.ToolCalling | ModelCapabilityFlags.ReasoningVariants)) == 0
        && ContextLimit is null or > 0
        && (DiscoverySource is null || DiscoverySource.Length is > 0 and <= 256
            && DiscoverySource == DiscoverySource.Trim() && !DiscoverySource.Any(char.IsControl))
        && ValidValues(ReasoningEfforts) && ValidValues(SpeedModes) && ValidValues(ExecutionModes)
        && (ReasoningEfforts.Count == 0 || (Flags & ModelCapabilityFlags.ReasoningVariants) != 0);

    public bool IsCurrent(DateTimeOffset now) => IsWellFormed
        && State == CapabilityState.Supported && ObservedAtUtc <= now && now < ExpiresAtUtc;

    public bool SupportsOptions(BackendType backend, DateTimeOffset now,
        string? reasoning, string? speed, string? mode) => IsCurrent(now)
        // Cursor parameters require per-model discovery, not a manual assertion (ТЗ §7).
        && (backend != BackendType.CursorAcp || Provenance != ModelProvenance.UserDefined)
        && Contains(ReasoningEfforts, reasoning) && Contains(SpeedModes, speed) && Contains(ExecutionModes, mode);

    private static bool Contains(IReadOnlyList<string> values, string? value) =>
        string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.Ordinal);

    private static bool ValidValues(IReadOnlyList<string>? values) => values is { Count: <= 64 }
        && values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
            && value == value.Trim() && !value.Any(char.IsControl))
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}

/// <summary>Persists evidence from explicit configuration or authenticated discovery.
/// The caller owns discovery/authentication; this store does not upgrade provenance.</summary>
public interface IModelCapabilityEvidenceStore
{
    /// <summary>Capture before discovery starts. A context captured after the response cannot
    /// establish which credentials or model identity produced it.</summary>
    Task<ModelCapabilityContext> CaptureContextAsync(string modelId, string accountId,
        CancellationToken cancellationToken = default);
    Task SaveAsync(ModelCapabilityEvidence evidence, ModelCapabilityContext context,
        CancellationToken cancellationToken = default);
    /// <summary>Explicit user declaration with optimistic concurrency. Source/time are assigned by
    /// the store; null expected means the editor observed no stored declaration.</summary>
    Task SaveUserDeclarationAsync(ModelCapabilityEvidence declaration, ModelCapabilityEvidence? expected,
        ModelCapabilityContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Opaque local identity revisions; neither credentials nor proof of native authentication.
/// Both revisions must come from one configuration snapshot captured before the operation.</summary>
public sealed record ModelCapabilityContext(string ModelId, string AccountId,
    string ModelRevision, string AccountRevision);

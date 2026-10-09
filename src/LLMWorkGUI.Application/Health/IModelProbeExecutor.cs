using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Health;

/// <summary>How a pinned model probe ended.</summary>
public enum ModelProbeOutcome
{
    /// <summary>The selected model answered the minimal operation, so a recovery may be verified.</summary>
    Succeeded,

    /// <summary>The probe ran and the observed result was a failure.</summary>
    Failed,

    /// <summary>
    /// This backend cannot run a pinned model probe at all. It is reported explicitly instead of being
    /// replaced by an operator judgement, so the scope stays out of routing (fail closed, ТЗ §6.10).
    /// </summary>
    Unsupported,

    /// <summary>A local credential/configuration gate refused the operation before HTTP dispatch.</summary>
    CredentialUnavailable
}

/// <summary>
/// A pinned model probe request. It deliberately carries only a secret <em>reference</em>: the executor
/// resolves the key itself, so no credential ever travels through the health layer or the audit.
/// </summary>
public sealed record ModelProbeRequest
{
    public required HealthScope Scope { get; init; }

    public required string ProviderProfileId { get; init; }

    /// <summary>Sanitized provider base URL, or <c>null</c> for a backend without an HTTP endpoint.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The model the probe must exercise, chosen by the operator behind the cost preview.</summary>
    public required string ModelId { get; init; }

    /// <summary>Opaque secret reference, or <c>null</c> when the provider needs no key.</summary>
    public string? ApiKeySecretReference { get; init; }
    public string? AccountId { get; init; }
    public IReadOnlyList<Providers.CustomProviderHeader> CustomHeaders { get; init; } = [];
}

/// <summary>Observed outcome of a pinned model probe.</summary>
public sealed record ModelProbeResult
{
    public required ModelProbeOutcome Outcome { get; init; }

    public HealthErrorClass? FailureClass { get; init; }

    public long? LatencyMs { get; init; }

    /// <summary>Sanitized endpoint that was called, or <c>null</c> when nothing was called.</summary>
    public string? SanitizedEndpoint { get; init; }

    /// <summary>Redacted detail, safe to persist in the append-only audit.</summary>
    public string? Detail { get; init; }

    public static ModelProbeResult Succeeded(string sanitizedEndpoint, long latencyMs) =>
        new()
        {
            Outcome = ModelProbeOutcome.Succeeded,
            SanitizedEndpoint = sanitizedEndpoint,
            LatencyMs = latencyMs
        };

    public static ModelProbeResult Failed(
        HealthErrorClass failureClass,
        string? sanitizedEndpoint,
        long? latencyMs,
        string detail) =>
        new()
        {
            Outcome = ModelProbeOutcome.Failed,
            FailureClass = failureClass,
            SanitizedEndpoint = sanitizedEndpoint,
            LatencyMs = latencyMs,
            Detail = detail
        };

    public static ModelProbeResult Unsupported(string detail) =>
        new()
        {
            Outcome = ModelProbeOutcome.Unsupported,
            Detail = detail
        };

    public static ModelProbeResult CredentialUnavailable(string detail) =>
        new() { Outcome = ModelProbeOutcome.CredentialUnavailable, Detail = detail };
}

/// <summary>
/// Runs the pinned model probe that verifies a recovery: it confirms the account is authenticated, the
/// selected model is the one actually answering, and a minimal operation completes (ТЗ §6.10).
///
/// A backend that cannot run this probe must say so through
/// <see cref="ModelProbeOutcome.Unsupported"/> rather than letting a connection-only check stand in for
/// it, because that would record a verified recovery nobody observed.
/// </summary>
public interface IModelProbeExecutor
{
    /// <summary>
    /// No-I/O transport capability check before admitting a probe. It does not prove authentication,
    /// model availability or native identity. Unupdated executors fail closed without being invoked.
    /// </summary>
    bool CanExecute(ModelProbeRequest request) => false;

    Task<ModelProbeResult> ExecuteAsync(
        ModelProbeRequest request,
        CancellationToken cancellationToken = default);
}

namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Request for the star-cliproxy OpenAI-compatible chat completions endpoint. The requested
/// provider/account/session fields are recorded as requested route evidence so the observed
/// provider/account/model/session reported back can be compared without trusting the request
/// (ТЗ §6.4, §6.11a).
/// </summary>
public sealed record StarCliProxyChatRequest
{
    /// <summary>Local stored-admission identifiers; omitted from HTTP JSON, never a permission grant.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LLMWorkGUI.Domain.ValueObjects.WorkflowReviewEgressContext? WorkflowReviewContext { get; init; }

    public required string ModelId { get; init; }

    public required IReadOnlyList<StarCliProxyChatMessage> Messages { get; init; }

    public string? RequestedProviderId { get; init; }

    public string? RequestedAccountId { get; init; }

    public string? RequestedSessionId { get; init; }

    public string? ReasoningEffort { get; init; }

    public bool Stream { get; init; } = true;

    public static StarCliProxyChatRequest Create(
        string modelId,
        IEnumerable<StarCliProxyChatMessage> messages,
        string? requestedProviderId = null,
        string? requestedAccountId = null,
        string? requestedSessionId = null,
        string? reasoningEffort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages.ToArray();

        return new StarCliProxyChatRequest
        {
            ModelId = modelId,
            Messages = materialized,
            RequestedProviderId = requestedProviderId,
            RequestedAccountId = requestedAccountId,
            RequestedSessionId = requestedSessionId,
            ReasoningEffort = reasoningEffort
        };
    }
}

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationModelRequest
{
    public AdaptationModelRequest(
        string routeId,
        string modelId,
        string systemPrompt,
        IReadOnlyList<AdaptationTurnMessage> messages)
    {
        RouteId = ApplicationGuard.NotBlank(routeId, nameof(routeId));
        ModelId = ApplicationGuard.NotBlank(modelId, nameof(modelId));
        SystemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        Messages = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();

        foreach (var message in Messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Adaptation turn messages must not contain null entries.", nameof(messages));
            }
        }
    }

    public string RouteId { get; }

    public string ModelId { get; }

    public string SystemPrompt { get; }

    public IReadOnlyList<AdaptationTurnMessage> Messages { get; }

    /// <summary>Local provenance identifiers to re-read; neither is permission or native identity.</summary>
    public string? ProjectId { get; init; }
    public string? SourceVersionId { get; init; }
    public Guid? AdmissionId { get; init; }
}

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationFollowUpRequest
{
    public AdaptationFollowUpRequest(
        string sessionId,
        string prompt,
        bool allowExpandedSemanticScope = false)
    {
        SessionId = ApplicationGuard.NotBlank(sessionId, nameof(sessionId));
        Prompt = ApplicationGuard.NotBlank(prompt, nameof(prompt));
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
    }

    public string SessionId { get; }

    public string Prompt { get; }

    public bool AllowExpandedSemanticScope { get; }
}

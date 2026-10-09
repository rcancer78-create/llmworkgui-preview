namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationModelResponse
{
    public AdaptationModelResponse(
        string rawText,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        if (promptTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(promptTokens), "Prompt tokens must not be negative.");
        }

        if (completionTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTokens), "Completion tokens must not be negative.");
        }

        RawText = rawText ?? throw new ArgumentNullException(nameof(rawText));
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
    }

    public string RawText { get; }

    public int? PromptTokens { get; }

    public int? CompletionTokens { get; }
}

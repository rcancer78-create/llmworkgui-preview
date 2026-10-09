namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// One OpenAI-compatible chat message sent to the star-cliproxy
/// <c>/v1/chat/completions</c> endpoint.
/// </summary>
public sealed record StarCliProxyChatMessage(string Role, string Content);

using System.Text;
namespace LLMWorkGUI.Application.Workflows;

/// <summary>One shared formatter so stored preparation hashes the same full text the invoker sends.</summary>
public static class AdaptationPromptEnvelope
{
    public static string Format(AdaptationModelRequest request)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            builder.AppendLine("=== SYSTEM INSTRUCTION ==="); builder.AppendLine(request.SystemPrompt.Trim());
            builder.AppendLine("=========================="); builder.AppendLine();
        }
        if (request.Messages.Count == 1 && string.Equals(request.Messages[0].Role,"user",StringComparison.OrdinalIgnoreCase))
            builder.Append(request.Messages[0].Content);
        else foreach (var message in request.Messages)
        { builder.AppendLine($"[{message.Role}]:"); builder.AppendLine(message.Content); builder.AppendLine(); }
        return builder.ToString();
    }
}

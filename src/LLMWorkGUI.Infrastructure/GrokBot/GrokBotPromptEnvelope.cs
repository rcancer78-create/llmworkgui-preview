using System.Text;
using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.GrokBot;

/// <summary>The exact text sent to the bridge, including provider instructions.
/// The native prompt must already include any Gateway transcript formatting.</summary>
public static class GrokBotPromptEnvelope
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Build(string nativePrompt)
    {
        if (string.IsNullOrWhiteSpace(nativePrompt)
            || nativePrompt.Length > GrokBotRestrictions.MaxPromptCharacters - GrokBotRestrictions.ReviewInstructions.Length)
            throw InvalidPrompt();
        var wirePrompt = GrokBotRestrictions.ReviewInstructions + nativePrompt;
        Validate(wirePrompt);
        return wirePrompt;
    }

    public static void Validate(string wirePrompt)
    {
        if (string.IsNullOrWhiteSpace(wirePrompt) || wirePrompt.Length > GrokBotRestrictions.MaxPromptCharacters)
            throw InvalidPrompt();
        try
        {
            if (StrictUtf8.GetByteCount(wirePrompt) > GrokBotRestrictions.MaxPromptUtf8Bytes) throw InvalidPrompt();
        }
        catch (EncoderFallbackException) { throw InvalidPrompt(); }
    }

    private static GatewayException InvalidPrompt() => GatewayException.Invalid(
        "Задание Grok Bot должно быть корректным текстом Unicode; полный текст с инструкцией провайдера ограничен 64 тыс. символов и 200 KB UTF-8.");
}

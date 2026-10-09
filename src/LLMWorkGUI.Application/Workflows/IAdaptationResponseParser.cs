namespace LLMWorkGUI.Application.Workflows;

public interface IAdaptationResponseParser
{
    /// <summary>
    /// Parses raw model output that may be wrapped in a Markdown JSON fence. Malformed JSON or
    /// schema violations never throw: they yield <see cref="AdaptationParsedResponse.IsValidJson"/>
    /// equal to <c>false</c> plus <see cref="Domain.Enums.AdaptationBlockerKind.InvalidSchema"/>.
    /// </summary>
    AdaptationParsedResponse Parse(string rawText);
}

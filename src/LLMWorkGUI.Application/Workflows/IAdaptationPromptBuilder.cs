using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public interface IAdaptationPromptBuilder
{
    /// <summary>
    /// Builds the system prompt for the adaptation turn. <paramref name="allowExpandedSemanticScope"/> is
    /// the scope the user actually selected for this run and is always stated verbatim, so the model is
    /// never told that the scope is off when the user confirmed it.
    /// </summary>
    string BuildSystemPrompt(AdaptationGoal goal, bool allowExpandedSemanticScope);

    string BuildUserPrompt(AdaptationPromptContext context);
}

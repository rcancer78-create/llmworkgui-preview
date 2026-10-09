using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// An immutable document template definition: the sections a generated document must contain, the
/// instructions handed to the generating model and the criteria that decide whether a draft is complete
/// (ROADMAP Phase 10E).
/// </summary>
public sealed class DocumentTemplateDefinition
{
    public DocumentTemplateDefinition(
        DocumentTemplateKind kind,
        string displayName,
        string description,
        IReadOnlyList<string> requiredSections,
        string modelInstructions,
        IReadOnlyList<string> completenessCriteria,
        string defaultTemplateBody)
    {
        Kind = kind;
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        Description = DomainGuard.NotBlank(description, nameof(description));
        RequiredSections = CopyValues(requiredSections, nameof(requiredSections));
        ModelInstructions = DomainGuard.NotBlank(modelInstructions, nameof(modelInstructions));
        CompletenessCriteria = CopyValues(completenessCriteria, nameof(completenessCriteria));
        DefaultTemplateBody = DomainGuard.NotBlank(defaultTemplateBody, nameof(defaultTemplateBody));
    }

    public DocumentTemplateKind Kind { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public IReadOnlyList<string> RequiredSections { get; }

    public string ModelInstructions { get; }

    public IReadOnlyList<string> CompletenessCriteria { get; }

    public string DefaultTemplateBody { get; }

    private static IReadOnlyList<string> CopyValues(
        IReadOnlyList<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = new string[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = DomainGuard.NotBlank(values[index], parameterName);
        }

        return copy;
    }
}

using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationPromptContext
{
    public AdaptationPromptContext(
        string sourceVersionId,
        string sourcePackageName,
        AdaptationGoal goal,
        SanitizedCapabilityCatalog catalog,
        IReadOnlyDictionary<string, string> fileContents,
        IReadOnlyList<string> excludedFiles,
        bool allowExpandedSemanticScope = false)
    {
        SourceVersionId = ApplicationGuard.NotBlank(sourceVersionId, nameof(sourceVersionId));
        SourcePackageName = ApplicationGuard.NotBlank(sourcePackageName, nameof(sourcePackageName));
        Goal = goal;
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        FileContents = new Dictionary<string, string>(
            fileContents ?? throw new ArgumentNullException(nameof(fileContents)),
            StringComparer.Ordinal);
        ExcludedFiles = (excludedFiles ?? throw new ArgumentNullException(nameof(excludedFiles))).ToArray();
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
    }

    public string SourceVersionId { get; }

    public string SourcePackageName { get; }

    public AdaptationGoal Goal { get; }

    /// <summary>The scope the user actually selected for this run; it is stated in the prompt verbatim.</summary>
    public bool AllowExpandedSemanticScope { get; }

    public SanitizedCapabilityCatalog Catalog { get; }

    public IReadOnlyDictionary<string, string> FileContents { get; }

    public IReadOnlyList<string> ExcludedFiles { get; }
}

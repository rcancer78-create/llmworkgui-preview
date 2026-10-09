using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Result of parsing a model adaptation response. When <see cref="IsValidJson"/> is <c>false</c> the
/// parser recovered no mappings and no file modifications: the only blocker is
/// <see cref="AdaptationBlockerKind.InvalidSchema"/>.
/// </summary>
public sealed record AdaptationParsedResponse
{
    public AdaptationParsedResponse(
        bool isValidJson,
        string? parseError,
        IReadOnlyList<SemanticRoleMapping> mappings,
        string rationale,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> blockers,
        IReadOnlyList<AdaptationBlockerKind> blockerKinds,
        IReadOnlyDictionary<string, string> fileModifications)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(blockers);
        ArgumentNullException.ThrowIfNull(blockerKinds);
        ArgumentNullException.ThrowIfNull(fileModifications);

        IsValidJson = isValidJson;
        ParseError = parseError;
        Mappings = mappings.ToArray();
        Rationale = rationale ?? throw new ArgumentNullException(nameof(rationale));
        Warnings = warnings.ToArray();
        Blockers = blockers.ToArray();
        BlockerKinds = blockerKinds.ToArray();
        FileModifications = new Dictionary<string, string>(fileModifications, StringComparer.Ordinal);
    }

    public bool IsValidJson { get; }

    public string? ParseError { get; }

    public IReadOnlyList<SemanticRoleMapping> Mappings { get; }

    public string Rationale { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Free-text blockers reported by the model in the top-level "blockers" array.</summary>
    public IReadOnlyList<string> Blockers { get; }

    /// <summary>Typed blockers: <see cref="AdaptationBlockerKind.InvalidSchema"/> plus per-mapping blocker kinds.</summary>
    public IReadOnlyList<AdaptationBlockerKind> BlockerKinds { get; }

    public IReadOnlyDictionary<string, string> FileModifications { get; }

    public static AdaptationParsedResponse Invalid(string parseError)
    {
        return new AdaptationParsedResponse(
            isValidJson: false,
            parseError,
            Array.Empty<SemanticRoleMapping>(),
            rationale: string.Empty,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { AdaptationBlockerKind.InvalidSchema },
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}

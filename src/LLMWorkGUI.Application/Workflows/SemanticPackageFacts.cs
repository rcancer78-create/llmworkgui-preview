namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// The bounded semantic facts of one real workflow package directory, extracted by
/// <see cref="SemanticPackageAnalyzer"/>. Facts are structural, never content: a document contributes
/// only its relative path and SHA-256 digest, so nothing from a package can leak into a prompt, a report
/// or a blocker message.
/// </summary>
public sealed record SemanticPackageFacts
{
    /// <summary>False when the package root could not be inspected; this is not an empty package.</summary>
    public bool InspectionSucceeded { get; init; } = true;

    /// <summary>
    /// Every root formal manifest of the package, ordered by path. <c>workflow.json</c> and
    /// <c>manifest.json</c> are both reported, because the activation parser reads <c>workflow.json</c>
    /// first and a second, matching manifest must not be able to mask a rewritten one.
    /// </summary>
    public IReadOnlyList<SemanticManifestFacts> Manifests { get; init; } =
        Array.Empty<SemanticManifestFacts>();

    /// <summary>SHA-256 digest of every semantic-bearing document, keyed by normalized relative path.</summary>
    public IReadOnlyDictionary<string, string> DocumentDigests { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Semantic-bearing files that exist but could not be read within the bounded analysis limits, or that
    /// the bounded file budget dropped before they were read, so no confident comparison against them is
    /// possible. These paths are never user-clearable: an incomplete analysis is a gap, not a finding.
    /// </summary>
    public IReadOnlyList<string> UnverifiableFiles { get; init; } = Array.Empty<string>();

    /// <summary>True when the bounded file budget was exhausted before the package was fully covered.</summary>
    public bool AnalysisTruncated { get; init; }

    /// <summary>An empty fact set, used when no package comparison is possible at all.</summary>
    public static SemanticPackageFacts Empty { get; } = new() { InspectionSucceeded = false };

    /// <summary>The facts of one root manifest, or null when this package does not declare it.</summary>
    public SemanticManifestFacts? FindManifest(string fileName)
    {
        foreach (var manifest in Manifests)
        {
            if (string.Equals(manifest.Path, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return manifest;
            }
        }

        return null;
    }
}

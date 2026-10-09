namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// The kind of workflow semantics a detected package difference belongs to (ТЗ §6.14). Every member is
/// derived from the real source and candidate packages; none of them can be produced from the
/// model-declared <c>isSemanticChange</c> flag alone.
/// </summary>
public enum SemanticChangeKind
{
    /// <summary>The <c>declaredRoles</c>/<c>roles</c> set of the formal manifest changed.</summary>
    DeclaredRoleSet,

    /// <summary>The <c>entrypoints</c> set of the formal manifest changed (stage surface).</summary>
    EntrypointSet,

    /// <summary>
    /// The formal manifest changed outside the known model-routing and provenance leaves, so an unparsed
    /// section (stages, quality gates, escalation, role policy) was rewritten. Every root manifest is
    /// compared on its own path, so this subject names the file that actually changed.
    /// </summary>
    ManifestStructure,

    /// <summary>A root formal manifest was added to or removed from the package.</summary>
    ManifestAvailability,

    /// <summary>
    /// The package declares more than one root formal manifest (<c>workflow.json</c> and
    /// <c>manifest.json</c>), so the executed semantics depend on file precedence rather than on a single
    /// declaration.
    /// </summary>
    ManifestPrecedence,

    /// <summary>
    /// A semantic-bearing document (role prompt, stage/quality/escalation prose) was added, removed or
    /// rewritten. The content is prose and is not machine-verifiable, so any difference is reported.
    /// </summary>
    SemanticDocument,

    /// <summary>
    /// A semantic-bearing file exists on both sides but could not be read within the bounded analysis
    /// limits, the bounded file budget dropped it, or the manifest could not be parsed. The comparison is
    /// therefore inconclusive and the guard fails closed instead of trusting the model. Never clearable.
    /// </summary>
    UnverifiableContent
}

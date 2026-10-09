namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// The bounded semantic facts of one root formal manifest of a package, extracted by
/// <see cref="SemanticPackageAnalyzer"/>.
/// <para>
/// A package may declare its workflow in <c>workflow.json</c> and/or in <c>manifest.json</c>, and
/// <see cref="IWorkflowManifestParser"/> reads <c>workflow.json</c> first. Every one of those files is
/// therefore read and reported separately, so the file the activation parser actually reads can never be
/// the one the semantic guard skipped.
/// </para>
/// </summary>
public sealed record SemanticManifestFacts
{
    public SemanticManifestFacts(
        string path,
        bool readable,
        string? structureForm,
        IReadOnlyList<string>? declaredRoles = null,
        IReadOnlyList<string>? entrypoints = null,
        bool declaredRolesTruncated = false,
        bool entrypointsTruncated = false)
    {
        Path = ApplicationGuard.NotBlank(path, nameof(path));
        Readable = readable;
        StructureForm = structureForm;
        DeclaredRoles = declaredRoles ?? (IReadOnlyList<string>)Array.Empty<string>();
        Entrypoints = entrypoints ?? (IReadOnlyList<string>)Array.Empty<string>();
        DeclaredRolesTruncated = declaredRolesTruncated;
        EntrypointsTruncated = entrypointsTruncated;
    }

    /// <summary>Normalized package-relative path of this root manifest, for example <c>workflow.json</c>.</summary>
    public string Path { get; }

    /// <summary>False when the file could not be read within the bounds or is not a JSON object.</summary>
    public bool Readable { get; }

    /// <summary>
    /// Canonical JSON of the manifest with the routing <i>leaves</i> of its top-level binding map removed,
    /// so a pure rebinding of a model route compares equal while every nested stage, quality-gate,
    /// escalation or role policy survives as text. Null when the manifest is absent or unreadable.
    /// </summary>
    public string? StructureForm { get; }

    /// <summary>Declared role names of this manifest, ordinal-sorted and de-duplicated.</summary>
    public IReadOnlyList<string> DeclaredRoles { get; }

    /// <summary>Declared entrypoint paths of this manifest, ordinal-sorted and de-duplicated.</summary>
    public IReadOnlyList<string> Entrypoints { get; }

    /// <summary>
    /// True when the manifest declares more raw role-name entries than the bounded analysis keeps, so
    /// <see cref="DeclaredRoles"/> is a prefix rather than the whole declaration. The comparison reports
    /// that as unverifiable content instead of trusting the prefix.
    /// </summary>
    public bool DeclaredRolesTruncated { get; }

    /// <summary>
    /// True when the manifest declares more raw entrypoint entries than the bounded analysis keeps, so
    /// <see cref="Entrypoints"/> is a prefix rather than the whole declaration. The comparison reports that
    /// as unverifiable content instead of trusting the prefix.
    /// </summary>
    public bool EntrypointsTruncated { get; }
}

using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// The full evidence set the ТЗ §6.14 semantic guard decides on. The role-mappings part comes from the
/// model response and the package part from the real source and candidate packages.
/// <para>
/// There is deliberately no approval input. Consent to a semantic change is a post-diff, per-issue
/// decision of the operator, and it is enforced by the activation gate against the freshly revalidated
/// issue list — not by anything a session flag, a model response or a package manifest could supply.
/// </para>
/// </summary>
public sealed record SemanticDiffRequest
{
    public SemanticDiffRequest(
        string? declaredRolesJson,
        IReadOnlyList<SemanticRoleMapping> mappings,
        bool allowExpandedSemanticScope,
        SemanticPackageFacts? sourcePackage = null,
        SemanticPackageFacts? candidatePackage = null)
    {
        DeclaredRolesJson = declaredRolesJson;
        Mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        AllowExpandedSemanticScope = allowExpandedSemanticScope;
        SourcePackage = sourcePackage;
        CandidatePackage = candidatePackage;
    }

    /// <summary>The declared role baseline of the source version, or null when it declares none.</summary>
    public string? DeclaredRolesJson { get; }

    /// <summary>The role mappings the model returned. An empty list is a role removal, not "no opinion".</summary>
    public IReadOnlyList<SemanticRoleMapping> Mappings { get; }

    /// <summary>
    /// The pre-send expanded-scope flag. It is prompt context only: it tells the model that a wider scope
    /// is intended, and it never suppresses a difference and never becomes a recorded approval.
    /// </summary>
    public bool AllowExpandedSemanticScope { get; }

    /// <summary>The real semantic facts of the source package, or null when no baseline is available.</summary>
    public SemanticPackageFacts? SourcePackage { get; }

    /// <summary>The real semantic facts of the candidate package, or null when no candidate is available.</summary>
    public SemanticPackageFacts? CandidatePackage { get; }
}

using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public interface ISemanticDiffEngine
{
    /// <summary>
    /// Compares the source version declared roles against the target role mappings and enforces the
    /// ТЗ §6.14 semantic guard. Semantic changes and role additions/removals against a non-empty
    /// declared baseline produce <see cref="AdaptationBlockerKind.DisallowedSemanticChange"/>.
    /// <paramref name="allowExpandedSemanticScope"/> is a pre-send prompt preference, never consent:
    /// it cannot clear a finding. An unreadable declared baseline always produces an unclearable blocker.
    /// </summary>
    SemanticDiffResult Compare(
        string? declaredRolesJson,
        IReadOnlyList<Domain.Entities.SemanticRoleMapping> mappings,
        bool allowExpandedSemanticScope);

    /// <summary>
    /// Decides the guard from the full evidence set: the role mappings, the real source and candidate
    /// package facts, and the semantic changes a user explicitly confirmed for exactly this content. The
    /// declared role baseline is still honoured when <see cref="SemanticDiffRequest.Mappings"/> is empty:
    /// an empty or unparseable mapping list against a non-empty baseline is a role removal.
    /// </summary>
    SemanticDiffResult Compare(SemanticDiffRequest request);
}

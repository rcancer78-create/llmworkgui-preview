namespace LLMWorkGUI.Application.Workflows;

public interface IAdaptationReferenceValidator
{
    AdaptationReferenceValidationResult Validate(
        IReadOnlyList<Domain.Entities.SemanticRoleMapping> mappings,
        SanitizedCapabilityCatalog catalog);
}

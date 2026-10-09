using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Validates adaptation target model references against the sanitized capability catalog. Three
/// conditions block: a model absent from <see cref="SanitizedCapabilityCatalog.Models"/>
/// (<see cref="AdaptationBlockerKind.MissingModel"/>), a model explicitly marked as not routable and a
/// routable model whose capabilities omit the mask required of the role; both of the latter report
/// <see cref="AdaptationBlockerKind.MissingCapability"/>. Degraded or forced-enabled models that
/// remain routable and satisfy the required mask are accepted.
/// </summary>
public sealed class AdaptationReferenceValidator : IAdaptationReferenceValidator
{
    public AdaptationReferenceValidationResult Validate(
        IReadOnlyList<SemanticRoleMapping> mappings,
        SanitizedCapabilityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(catalog);

        var issues = new List<AdaptationValidationIssue>();

        foreach (var mapping in mappings)
        {
            var model = catalog.Models.FirstOrDefault(candidate => string.Equals(
                candidate.ModelId,
                mapping.TargetModelId,
                StringComparison.Ordinal));

            if (model is null)
            {
                issues.Add(new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingModel,
                    mapping.Role.ToString(),
                    $"Model '{mapping.TargetModelId}' not found in active catalog."));
                continue;
            }

            if (!model.IsRoutable)
            {
                issues.Add(new AdaptationValidationIssue(
                    AdaptationBlockerKind.MissingCapability,
                    mapping.Role.ToString(),
                    $"Model '{mapping.TargetModelId}' is marked not routable."));
                continue;
            }

            issues.AddRange(ValidateRequiredCapabilities(mapping, model));
        }

        return issues.Count == 0
            ? AdaptationReferenceValidationResult.Empty
            : new AdaptationReferenceValidationResult(issues);
    }

    /// <summary>
    /// Reports a blocker when a present, routable catalog row does not carry the capability mask the
    /// role requires. The mask comes from the single closed
    /// <see cref="WorkflowRoleRequiredCapabilities"/> table. The message names no catalog vintage so
    /// both the session validator and the activation validator can use it verbatim.
    /// </summary>
    internal static IReadOnlyList<AdaptationValidationIssue> ValidateRequiredCapabilities(
        SemanticRoleMapping mapping,
        SanitizedModelInfo model)
    {
        if (WorkflowRoleRequiredCapabilities.IsSatisfiedBy(mapping.Role, model.Capabilities))
        {
            return Array.Empty<AdaptationValidationIssue>();
        }

        return new[]
        {
            new AdaptationValidationIssue(
                AdaptationBlockerKind.MissingCapability,
                mapping.Role.ToString(),
                $"Model '{mapping.TargetModelId}' is routable but does not provide the capability "
                + $"'{WorkflowRoleRequiredCapabilities.RequiredFor(mapping.Role)}' required by role "
                + $"'{mapping.Role}' (declared capabilities: '{model.Capabilities}').")
        };
    }
}

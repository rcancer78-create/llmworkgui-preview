using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

/// <summary>Shared metadata eligibility for route selection and egress. Eligibility is not fragment approval.</summary>
public static class ProviderDataPolicy
{
    public static DataClassificationGateDecision Evaluate(DataClassification projectClass, string? profileId,
        ProviderProfile? profile, BackendType? expectedBackend = null)
    {
        if (!Enum.IsDefined(projectClass))
            return DataClassificationGateDecision.Blocked("Project data classification is unknown; dispatch is blocked.");
        if (string.IsNullOrWhiteSpace(profileId) || profile is null || profile.Id != profileId)
            return DataClassificationGateDecision.Blocked($"Provider profile '{profileId ?? "<none>"}' is missing or unavailable; verified classification metadata is required.");
        if (!profile.IsEnabled || !Enum.IsDefined(profile.Backend) || (expectedBackend is { } backend && profile.Backend != backend))
            return DataClassificationGateDecision.Blocked($"Provider profile '{profileId}' is disabled or belongs to another backend.");
        if (!Enum.IsDefined(profile.MaxDataClass))
            return DataClassificationGateDecision.Blocked($"Provider profile '{profileId}' has an unknown maximum data classification.");
        if (projectClass > profile.MaxDataClass)
            return DataClassificationGateDecision.Blocked($"Data classification violation: project data class '{projectClass}' exceeds provider profile '{profileId}' maximum allowed data class '{profile.MaxDataClass}'.");
        return DataClassificationGateDecision.Allowed();
    }
}

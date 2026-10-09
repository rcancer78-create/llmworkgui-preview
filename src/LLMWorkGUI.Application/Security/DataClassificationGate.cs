using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

public sealed class DataClassificationGate : IDataClassificationGate
{
    private const string RestrictedExplanation =
        "Restricted project data is forbidden from automatic routing and dispatch; only ManualOnly with verified per-fragment preview is permitted (ТЗ §6.5, THREAT_MODEL.md §3, ADR-0004 §6.2).";

    private readonly IProviderProfileRepository? _providerProfileRepository;

    public DataClassificationGate(IProviderProfileRepository? providerProfileRepository = null)
    {
        _providerProfileRepository = providerProfileRepository;
    }

    public async Task<DataClassificationGateDecision> EvaluateAsync(
        DataClassification projectDataClass,
        string? providerProfileId,
        bool isManualOnly = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (projectDataClass == DataClassification.Restricted)
        {
            return DataClassificationGateDecision.Blocked(RestrictedExplanation);
        }

        var profile = _providerProfileRepository is not null && !string.IsNullOrWhiteSpace(providerProfileId)
            ? await _providerProfileRepository.GetByIdAsync(providerProfileId, cancellationToken).ConfigureAwait(false)
            : null;
        return ProviderDataPolicy.Evaluate(projectDataClass, providerProfileId, profile);
    }
}

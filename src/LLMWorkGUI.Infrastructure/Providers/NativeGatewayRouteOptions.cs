using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

internal static class NativeGatewayRouteOptions
{
    internal static bool RequiresChatDeclaration(string profileId, string provenance) =>
        profileId == GatewayCatalogMapper.ProviderId(ProviderKind.Cursor) && provenance == nameof(ModelProvenance.UserDefined);

    internal static bool IsSupported(IReadOnlyList<ModelCapabilityEvidence> evidence, NativeGatewayRouteBinding binding, DateTimeOffset now, bool requireChatDeclaration = false)
    {
        if (requireChatDeclaration && !evidence.Any(item => item.ModelId == binding.ModelId
            && item.AccountId == binding.AccountId && item.Provenance == ModelProvenance.UserDefined
            && item.Flags.HasFlag(ModelCapabilityFlags.Chat) && item.IsCurrent(now))) return false;
        if (binding.ReasoningEffort is null) return true;
        // Codex is the shipped transport with both explicit reasoning CLI support and scoped discovery.
        return binding.ProviderProfileId == GatewayCatalogMapper.ProviderId(ProviderKind.Codex)
            && binding.ReasoningEffort.Length is >= 2 and <= 12 && binding.ReasoningEffort.All(char.IsAsciiLetterLower)
            && evidence.Any(item => item.ModelId == binding.ModelId && item.AccountId == binding.AccountId
                && item.Flags.HasFlag(ModelCapabilityFlags.ReasoningVariants)
                && item.SupportsOptions(BackendType.NativeGateway, now, binding.ReasoningEffort, null, null));
    }
}

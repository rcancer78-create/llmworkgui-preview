using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Information about an account discovered via a backend or plugin bridge.
/// </summary>
public sealed record DiscoveredAccountInfo(
    string Id,
    string DisplayName,
    string? ProviderNativeId,
    AuthState AuthState,
    string? Email = null,
    bool IsDefault = false);

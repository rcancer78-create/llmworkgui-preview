using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>Operator settings only. Never proves native authentication or response origin.</summary>
public sealed record AdaptationAccountConfiguration(string AccountId, string ProviderProfileId,
    string? SecretReference, SecretReferenceState SecretState, bool IsRegisteredKey,
    string? NativeProviderId, string? MappingSecretReference, bool HasOwnedExecution)
{
    public bool IsKeyUsable => IsRegisteredKey && SecretState == SecretReferenceState.Active;
    public bool IsMappingCurrent => IsKeyUsable && NativeProviderId is not null && MappingSecretReference == SecretReference;
}

public interface IAdaptationAccountConfigurationService
{
    Task<AdaptationAccountConfiguration> ReadConfigurationAsync(string profileId, string accountId, CancellationToken token = default);
    Task SaveKeyAsync(AdaptationAccountConfiguration expected, string enteredKey, CancellationToken token = default);
    Task ConfigureProviderAsync(AdaptationAccountConfiguration expected, string nativeProviderId, CancellationToken token = default);
}

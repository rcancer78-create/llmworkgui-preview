namespace LLMWorkGUI.Application.Providers;

public sealed record NativeGatewayRouteBinding(string ProviderProfileId, string AccountId, string ModelId,
    string NativeAccountId, string NativeModelId, string? ReasoningEffort = null);

public sealed record NativeGatewayRouteOption(string Id, NativeGatewayRouteBinding Binding,
    string ProviderName, string AccountName, string ModelName)
{
    public string Display => $"{ProviderName} · {AccountName} · {ModelName}" + (Binding.ReasoningEffort is null ? "" : $" · {Binding.ReasoningEffort}");
    public string Limitations => Binding.ProviderProfileId == GrokBotRestrictions.ProviderProfileId
        ? GrokBotRestrictions.Notice : string.Empty;
}

/// <summary>Reads eligible saved routes for one project. Does not discover accounts or invoke native clients.</summary>
public interface INativeGatewayRouteCatalog
{
    Task<IReadOnlyList<NativeGatewayRouteOption>> ListAsync(string projectId, string rootPath, CancellationToken cancellationToken = default);
}

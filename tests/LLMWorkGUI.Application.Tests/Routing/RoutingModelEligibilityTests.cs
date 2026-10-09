using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Tests.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Routing;

public sealed class RoutingModelEligibilityTests
{
    private const string ProfileId = "prov-model";
    private const string AccountId = "acc-model";

    [Fact]
    public async Task ASupportedRoute_IsEligible()
    {
        var decision = await SelectAsync(Configuration(Supported("gpt-4o"), Route()));

        Assert.True(decision.IsSuccess);
        Assert.Equal(AccountId, decision.SelectedAccount!.Id);
    }

    [Fact]
    public async Task AnUnknownModel_IsRejected()
    {
        var decision = await SelectAsync(Configuration(Supported("other-model"), Route()));

        Assert.False(decision.IsSuccess);
        Assert.Contains("not configured", Reason(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCapability_IsNotTreatedAsSupport()
    {
        var decision = await SelectAsync(Configuration(Supported("gpt-4o", CapabilityState.Unknown), Route()));

        Assert.False(decision.IsSuccess);
        Assert.Contains("not a supported model", Reason(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelWithoutARouteForTheAccount_IsUnavailable()
    {
        var configuration = Configuration(
            Supported("gpt-4o"),
            Route(accountId: "someone-else"));

        var decision = await SelectAsync(configuration);

        Assert.False(decision.IsSuccess);
        Assert.Contains("not available to this account", Reason(decision), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestedReasoningEffort_MustMatchTheSavedRoute()
    {
        var mismatched = await SelectAsync(Configuration(Supported("gpt-4o"), Route()), reasoning: "high");
        var matched = await SelectAsync(
            Configuration(Supported("gpt-4o"), Route(reasoning: "high")) with
            {
                Capabilities = [new("model-row", AccountId, CapabilityState.Supported, ModelProvenance.ProviderReported,
                    DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5),
                    ModelCapabilityFlags.ReasoningVariants, ["high"], [], [])]
            },
            reasoning: "high");

        Assert.False(mismatched.IsSuccess);
        Assert.Contains("not configured for model", Reason(mismatched), StringComparison.Ordinal);
        Assert.True(matched.IsSuccess);
    }

    private static async Task<RoutingDecision> SelectAsync(
        ModelRouteConfiguration configuration,
        string? reasoning = null)
    {
        var accounts = new InMemoryAccountRepository();
        await accounts.SaveAsync(new Account(
            AccountId, ProfileId, "Model account", null, AuthState.Valid, 10, true,
            HealthState.Healthy, null, null, 2, null));
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile(
            ProfileId, "Model provider", BackendType.OpenCode, null, null,
            DataClassification.PrivateSource, true));
        var engine = new RoutingEngine(
            accounts,
            new InMemoryQuotaSnapshotRepository(),
            providerProfileRepository: profiles,
            modelEligibility: new ConfiguredRouteModelEligibility(new FixedConfiguration(configuration)));

        return await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = ProfileId,
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            ReasoningEffort = reasoning
        });
    }

    private static string Reason(RoutingDecision decision) =>
        Assert.Single(decision.RejectedCandidates).Reason;

    private static ConfiguredModel Supported(string nativeId, CapabilityState capability = CapabilityState.Supported) =>
        new("model-row", ProfileId, BackendType.OpenCode, nativeId, "GPT", capability, ModelProvenance.UserDefined, true);

    private static ConfiguredRoute Route(string accountId = AccountId, string? reasoning = null) =>
        new("route-1", ProfileId, accountId, "model-row", null, DataClassification.PrivateSource, true, 0,
            reasoning, null, BackendType.OpenCode);

    private static ModelRouteConfiguration Configuration(ConfiguredModel model, ConfiguredRoute route) =>
        new([], [], [model], [route]);

    private sealed class FixedConfiguration(ModelRouteConfiguration configuration) : IModelRouteConfigurationService
    {
        public Task<ModelRouteConfiguration> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task<string> SaveModelAsync(SaveModelConfiguration request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SaveRouteAsync(SaveRouteConfiguration request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

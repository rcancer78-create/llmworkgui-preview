using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Security;

public sealed class DataClassificationGateTests
{
    [Theory]
    [InlineData("no-repository")]
    [InlineData("no-id")]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("invalid-maximum")]
    [InlineData("invalid-class")]
    public async Task UnknownOrDisabledProviderMetadataCannotAuthorizePublicData(string scenario)
    {
        var repository = new InMemoryProviderProfileRepository();
        if (scenario is "disabled" or "invalid-maximum" or "invalid-class")
            repository.Save(new ProviderProfile("provider", "Provider", BackendType.OpenCode, null, null,
                scenario == "invalid-maximum" ? (DataClassification)99 : DataClassification.PublicSource, scenario != "disabled"));
        var gate = new DataClassificationGate(scenario == "no-repository" ? null : repository);
        var decision = await gate.EvaluateAsync(scenario == "invalid-class" ? (DataClassification)(-1) : DataClassification.PublicSource,
            scenario == "no-id" ? null : "provider");
        Assert.False(decision.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.Explanation));
    }

    private static ProviderProfile Profile(string id, DataClassification maxDataClass) =>
        new(id, id, BackendType.OpenCode, null, null, maxDataClass, true);

    [Fact]
    public async Task EvaluateAsync_PublicSource_IsAllowed()
    {
        var repository = new InMemoryProviderProfileRepository();
        repository.Save(Profile("custom-provider", DataClassification.PublicSource));
        var gate = new DataClassificationGate(repository);

        var decision = await gate.EvaluateAsync(DataClassification.PublicSource, "custom-provider");

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_PrivateSourceForPrivateSourceProfile_IsAllowed()
    {
        var repository = new InMemoryProviderProfileRepository();
        repository.Save(Profile("prov-private", DataClassification.PrivateSource));

        var gate = new DataClassificationGate(repository);

        var decision = await gate.EvaluateAsync(DataClassification.PrivateSource, "prov-private");

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_PrivateSourceForPublicSourceProfile_IsBlocked()
    {
        var repository = new InMemoryProviderProfileRepository();
        repository.Save(Profile("prov-public", DataClassification.PublicSource));

        var gate = new DataClassificationGate(repository);

        var decision = await gate.EvaluateAsync(DataClassification.PrivateSource, "prov-public");

        Assert.False(decision.IsAllowed);
        Assert.Contains("prov-public", decision.Explanation);
        Assert.Contains(nameof(DataClassification.PrivateSource), decision.Explanation);
        Assert.Contains(nameof(DataClassification.PublicSource), decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_Restricted_IsBlockedEvenForRestrictedProfile()
    {
        var repository = new InMemoryProviderProfileRepository();
        repository.Save(Profile("prov-restricted", DataClassification.Restricted));

        var gate = new DataClassificationGate(repository);

        var decision = await gate.EvaluateAsync(DataClassification.Restricted, "prov-restricted");

        Assert.False(decision.IsAllowed);
        Assert.Contains("Restricted", decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_RestrictedWithoutProviderProfile_IsBlocked()
    {
        var gate = new DataClassificationGate();

        var decision = await gate.EvaluateAsync(DataClassification.Restricted, null);

        Assert.False(decision.IsAllowed);
        Assert.Contains("Restricted", decision.Explanation);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("mirasim")]
    [InlineData("cursor")]
    public async Task EvaluateAsync_SystemPlaceholder_WithoutProfileInRepo_BlocksPrivateSourceFailClosed(string providerProfileId)
    {
        var gate = new DataClassificationGate(new InMemoryProviderProfileRepository());

        var decision = await gate.EvaluateAsync(DataClassification.PrivateSource, providerProfileId);

        Assert.False(decision.IsAllowed);
        Assert.Contains(providerProfileId, decision.Explanation);
        Assert.Contains("metadata", decision.Explanation);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("mirasim")]
    [InlineData("cursor")]
    public async Task EvaluateAsync_SystemPlaceholder_WithProfileInRepo_AllowsPrivateSource(string providerProfileId)
    {
        var repository = new InMemoryProviderProfileRepository();
        repository.Save(Profile(providerProfileId, DataClassification.PrivateSource));

        var gate = new DataClassificationGate(repository);

        var decision = await gate.EvaluateAsync(DataClassification.PrivateSource, providerProfileId);

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_SystemPlaceholder_StillBlocksRestricted()
    {
        var gate = new DataClassificationGate();

        var decision = await gate.EvaluateAsync(DataClassification.Restricted, "mirasim");

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task EvaluateAsync_MissingProfile_BlocksPrivateSourceFailClosed()
    {
        var gate = new DataClassificationGate(new InMemoryProviderProfileRepository());

        var decision = await gate.EvaluateAsync(DataClassification.PrivateSource, "missing-provider");

        Assert.False(decision.IsAllowed);
        Assert.Contains("missing-provider", decision.Explanation);
        Assert.Contains("metadata", decision.Explanation);
    }

    [Fact]
    public async Task EvaluateAsync_MissingProfile_BlocksPublicSource()
    {
        var gate = new DataClassificationGate(new InMemoryProviderProfileRepository());

        var decision = await gate.EvaluateAsync(DataClassification.PublicSource, "missing-provider");

        Assert.False(decision.IsAllowed);
    }
}

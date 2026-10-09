using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class ModelRouterCompositeIdTests
{
    private static readonly AccountProfile[] Accounts =
    [
        new() { Id = "personal", Provider = ProviderKind.Codex, IsActive = true },
        new() { Id = "work", Provider = ProviderKind.Codex, DefaultModel = "gpt-5.5" },
        new() { Id = "other-provider", Provider = ProviderKind.Claude }
    ];

    [Theory]
    [InlineData("codex/personal/gpt-5.5", "gpt-5.5")]
    [InlineData("codex/personal/vendor/model", "vendor/model")]
    [InlineData("CODEX/PERSONAL/vendor/model", "vendor/model")]
    [InlineData("codex/personal", "gpt-5.5")]
    public void ExplicitAccountOverridesEmbeddedAccountWithoutChangingNativeModel(string model, string expectedNative)
    {
        var route = Resolve(model, "work");

        Assert.Equal("work", route.Account.Id);
        Assert.Equal(expectedNative, route.NativeModel);
    }

    [Theory]
    [InlineData("vendor/model", "vendor/model")]
    [InlineData("codex/vendor/model", "vendor/model")]
    [InlineData("codex/other-provider/model", "other-provider/model")]
    [InlineData("work/vendor/model", "work/vendor/model")]
    [InlineData("vendor//model/", "vendor//model/")]
    [InlineData("codex/vendor//model/", "vendor//model/")]
    public void ExplicitAccountPreservesSlashContainingNativeNames(string model, string expectedNative)
    {
        Assert.Equal(expectedNative, Resolve(model, "work").NativeModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximumLengthCatalogIdRoundTripsWithAndWithoutAccountOverride(bool overrideAccount)
    {
        var source = new AccountProfile { Id = new string('a', 64), Provider = ProviderKind.Antigravity, IsActive = true };
        var target = new AccountProfile { Id = "work", Provider = source.Provider };
        var native = "vendor/" + new string('m', 153);
        var catalogId = ModelRouter.ModelId(source, native);
        Assert.True(JsonAccountStore.IsValidId(source.Id));
        Assert.True(ModelRouter.IsValidModelName(native));
        Assert.True(catalogId.Length > 160);

        var route = ModelRouter.Resolve(new ChatRequest { Model = catalogId, AccountId = overrideAccount ? target.Id : null },
            [source, target], source.Provider, _ => null);

        Assert.Equal(overrideAccount ? target.Id : source.Id, route.Account.Id);
        Assert.Equal(native, route.NativeModel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("codex/")]
    [InlineData("codex/personal/")]
    public void CompositeRouteDoesNotExtendTheNativeModelLimit(string prefix)
    {
        var model = prefix + new string('m', 161);

        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => Resolve(model, "work")).Kind);
    }

    [Fact]
    public void BareModelWithoutAccountStillEnforcesItsOwnLengthLimit()
    {
        Assert.Equal(GatewayErrorKind.InvalidRequest,
            Assert.Throws<GatewayException>(() => Resolve(new string('m', 161))).Kind);
    }

    [Theory]
    [InlineData("codex/personal/-option")]
    [InlineData("codex/personal/vendor\nmodel")]
    [InlineData("codex/personal/vendor\rmodel")]
    [InlineData("codex/personal/vendor model")]
    public void CompositeRoutesRejectUnsafeNativeNames(string model)
    {
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() => Resolve(model, "work")).Kind);
    }

    [Theory]
    [InlineData("model\n")]
    [InlineData("model\r")]
    [InlineData("model\r\n")]
    public void NativeNameValidationDoesNotAcceptTrailingNewlines(string model)
    {
        Assert.False(ModelRouter.IsValidModelName(model));
    }

    private static RouteResult Resolve(string model, string? accountId = null) => ModelRouter.Resolve(
        new ChatRequest { Model = model, AccountId = accountId }, Accounts, ProviderKind.Codex, _ => null);
}

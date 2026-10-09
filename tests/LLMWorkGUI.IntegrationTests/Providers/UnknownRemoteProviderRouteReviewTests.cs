using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class UnknownRemoteProviderRouteReviewTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void RemoteUnknownCannotBeAdmittedAsNativeProviderBinding(int provider, bool admitted)
    {
        var kind = (ProviderKind)provider;
        var binding = new NativeGatewayRouteBinding(GatewayCatalogMapper.ProviderId(kind),
            GatewayCatalogMapper.AccountId(kind, "owned-account"), GatewayCatalogMapper.ModelId(kind, "owned-model"),
            "owned-account", "owned-model");
        Assert.Equal(admitted, NativeGatewayRouteQuery.HasValidBinding(binding));
    }
}

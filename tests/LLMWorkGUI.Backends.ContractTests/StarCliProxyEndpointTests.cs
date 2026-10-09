using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class StarCliProxyEndpointTests
{
    [Theory]
    [InlineData("http://example.invalid:8300/")]
    [InlineData("http://192.0.2.10:8300/")]
    public void EndpointRejectsNonLoopbackBeforeItCanBeUsedToSendCredentials(string uri)
    {
        Assert.Throws<ArgumentException>(() => new StarCliProxyEndpoint(new Uri(uri), "synthetic-key"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8300")]
    [InlineData("http://localhost:8300")]
    public void EndpointAcceptsConfiguredLoopbackHosts(string uri)
    {
        Assert.Equal(uri + "/", new StarCliProxyEndpoint(new Uri(uri)).BaseUrl.AbsoluteUri);
    }
}

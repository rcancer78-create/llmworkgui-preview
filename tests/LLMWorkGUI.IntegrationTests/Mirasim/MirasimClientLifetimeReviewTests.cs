using System.Net;
using System.Net.Sockets;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed class MirasimClientLifetimeReviewTests
{
    [Fact]
    public async Task DisposingTheContainerRetiresItsOwnedHealthHttpClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var services = new ServiceCollection().AddMirasimBackend();
        services.Configure<MirasimOptions>(options =>
        {
            options.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            options.RequestTimeout = TimeSpan.FromMilliseconds(100);
        });
        IMirasimClient client;
        using (var provider = services.BuildServiceProvider())
            client = provider.GetRequiredService<IMirasimClient>();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ProbeHealthAsync());
        Assert.False(listener.Pending(), "An already disposed owner must not initiate another HTTP request.");
    }

    [Fact]
    public async Task DisposingAClientDoesNotDisposeAnExternallySuppliedSharedHttpClient()
    {
        using var handler = new CanaryHandler();
        using var http = new HttpClient(handler);
        var client = new MirasimClient(http, Options.Create(new MirasimOptions()));
        if ((object)client is IDisposable disposable) disposable.Dispose();
        using var response = await http.GetAsync("http://127.0.0.1/shared-client-contract");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.Disposals);
    }

    private sealed class CanaryHandler : HttpMessageHandler
    {
        public int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }
}

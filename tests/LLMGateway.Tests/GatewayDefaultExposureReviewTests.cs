using System.Net;
using System.Net.Http.Headers;
using LLMGateway.Core;
using LLMGateway.Server;

namespace LLMGateway.Tests;

public sealed class GatewayDefaultExposureReviewTests
{
    [Fact]
    public async Task AuthenticatedDefaultServerDoesNotExposeAccountManagement()
    {
        var options = new GatewayServerOptions { ApiKey = "synthetic-review-key" };
        Assert.True(options.LoopbackOnly);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([]), [new FakeAdapter()],
            new GatewayOptions { WorkspaceDirectory = Path.GetTempPath() });
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", options);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var management = await http.GetAsync("/v1/gateway/accounts");
        Assert.Equal(HttpStatusCode.NotFound, management.StatusCode);
        using var health = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}

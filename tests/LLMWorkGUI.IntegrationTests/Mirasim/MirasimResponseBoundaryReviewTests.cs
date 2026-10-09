using System.Net;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

// Controlled HTTP content proves local resource bounds, not an actual external Mirasim instance.
public sealed class MirasimResponseBoundaryReviewTests
{
    [Fact]
    public async Task OversizedHealthJsonIsUnavailableInsteadOfAnUnboundedSuccess()
    {
        using var http = new HttpClient(new ContentHandler(HttpStatusCode.OK,
            new string(' ', 1024 * 1024 + 8192) + "{\"ok\":true}"));
        using var client = new MirasimClient(http, Options.Create(new MirasimOptions()));
        Assert.False((await client.ProbeHealthAsync()).Ok);
    }

    [Fact]
    public async Task OversizedErrorBodyDoesNotBecomeAnUnboundedErrorCode()
    {
        using var http = new HttpClient(new ContentHandler(HttpStatusCode.InternalServerError,
            "{\"error\":\"" + new string('x', 128 * 1024) + "\"}"));
        using var client = new MirasimClient(http, Options.Create(new MirasimOptions()));
        var error = await Assert.ThrowsAsync<MirasimClientException>(() => client.ProbeAuthenticatedEndpointAsync());
        Assert.Null(error.ErrorCode);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task SmallValidHealthJsonRemainsUsable()
    {
        using var http = new HttpClient(new ContentHandler(HttpStatusCode.OK, "{\"ok\":true,\"instance\":\"owned\"}"));
        using var client = new MirasimClient(http, Options.Create(new MirasimOptions()));
        var health = await client.ProbeHealthAsync();
        Assert.True(health.Ok);
        Assert.Equal("owned", health.InstanceId);
    }

    private sealed class ContentHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

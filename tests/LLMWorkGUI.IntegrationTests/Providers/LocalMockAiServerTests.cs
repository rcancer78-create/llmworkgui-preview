using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.MockServers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public class LocalMockAiServerTests
{
    [Fact]
    public async Task LocalMockAiServer_StartsAndServesModels()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"{server.BaseUrl}/models");

        Assert.True(response.IsSuccessStatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(content) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal("list", node["object"]?.GetValue<string>());

        var data = node["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.True(data.Count >= 2);
    }

    [Fact]
    public async Task LocalMockAiServer_WithExpectedApiKey_EnforcesBearerAuth()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-valid-mock-token";
        await server.StartAsync();

        using var client = new HttpClient();

        // 1. Without auth -> 401
        var unauthResponse = await client.GetAsync($"{server.BaseUrl}/models");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthResponse.StatusCode);

        // 2. With wrong auth -> 401
        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/models");
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-wrong-token");
        var wrongResponse = await client.SendAsync(wrongRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrongResponse.StatusCode);

        // 3. With correct auth -> 200
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/models");
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-valid-mock-token");
        var validResponse = await client.SendAsync(validRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, validResponse.StatusCode);
    }

    [Fact]
    public async Task LocalMockAiServer_WithSimulatedStatusCode_ReturnsConfiguredStatus()
    {
        await using var server = new LocalMockAiServer();
        server.SimulatedStatusCode = 503;
        await server.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"{server.BaseUrl}/models");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task LocalMockAiServer_CapturesCustomHeaders()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/models");
        request.Headers.Add("X-Organization-Id", "org-test-123");
        var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(server.LastReceivedHeaders.ContainsKey("X-Organization-Id"));
        Assert.Equal("org-test-123", server.LastReceivedHeaders["X-Organization-Id"]);
    }
}

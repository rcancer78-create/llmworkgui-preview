using System.Net.Http.Json;
using LLMGateway.Core;
using LLMGateway.Server;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class GatewayReviewRegressionTests
{
    public static IEnumerable<object[]> PublicErrorCases() =>
        Enum.GetValues<GatewayErrorKind>().SelectMany(kind => new[] { new object[] { kind, false }, new object[] { kind, true } });

    [Theory]
    [MemberData(nameof(PublicErrorCases))]
    public async Task AdapterErrorCategoryNeverAuthorizesPublishingItsMessage(GatewayErrorKind kind, bool stream)
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter { Error = new GatewayException(kind, "private-path-secret-canary") });
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0",
            new() { ApiKey = "fixture", SanitizeErrors = true });
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        { model = "codex/work/model", stream, messages = new[] { new { role = "user", content = "fixture" } } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.DoesNotContain("private-path-secret-canary", body);
        Assert.DoesNotContain("[DONE]", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyOutputLimitCannotHideFailedNativeCleanup(bool stopSequence)
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter
        { Output = "partial STOP more output", CleanupError = new GatewayException(GatewayErrorKind.Upstream, "cleanup-unconfirmed") });
        var request = Request();
        if (stopSequence) request.Stop = ["STOP"];
        else request.MaxOutputTokens = 1;
        var completed = false;
        var error = await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            await foreach (var update in gateway.StreamAsync(request)) completed |= update.Kind == ChatUpdateKind.Completed;
        });
        Assert.False(completed);
        Assert.Equal("cleanup-unconfirmed", error.Message);
    }
}

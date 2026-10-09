using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Server;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class GatewayReviewRegressionTests
{
    [Theory]
    [InlineData("--mode agent")]
    [InlineData("--mode=default")]
    [InlineData("--mode unknown")]
    [InlineData("--mode")]
    [InlineData("-s workspace-write")]
    [InlineData("-s=danger-full-access")]
    [InlineData("-sworkspace-write")]
    [InlineData("-c sandbox_mode=\"danger-full-access\"")]
    [InlineData("-csandbox_mode=\"danger-full-access\"")]
    [InlineData("--config sandbox_mode=workspace-write")]
    [InlineData("--config=approval_policy=never")]
    [InlineData("--permission-mode default")]
    [InlineData("--approval-mode full-auto")]
    [InlineData("--ask-for-approval never")]
    [InlineData("-anever")]
    [InlineData("--mode plan --mode agent")]
    [InlineData("--sandbox read-only --sandbox workspace-write")]
    public void PolicyOverridesAreRejectedBeforeLaunch(string arguments) =>
        Assert.Throws<GatewayException>(() => CliArguments.RejectUnsafe(CliArguments.Parse(arguments)));

    [Theory]
    [InlineData("--mode ask --color never")]
    [InlineData("--mode plan --sandbox read-only")]
    [InlineData("-sread-only -aon-request")]
    [InlineData("--model model-id -m model-id --effort high")]
    public void SafeExtraArgumentsRemainUsable(string arguments) => CliArguments.RejectUnsafe(CliArguments.Parse(arguments));

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    public void UnresolvedScriptLaunchIsRefusedBeforeProcessCreation(string extension)
    {
        using var directory = new TestDirectory();
        // The executable deliberately does not exist: rejection must happen before trying to start it.
        var launch = new NativeLaunch(new("missing-executable", [], LaunchKind.Script, "wrapper" + extension),
            ["plain prompt"], new Dictionary<string, string?>(), directory.Root);
        var error = Assert.Throws<GatewayException>(() => NativeProcess.Start(launch));
        Assert.Equal(GatewayErrorKind.Unsupported, error.Kind);
    }

    [Fact]
    public async Task UnauthenticatedLoopbackCannotUseApiOrMutateAccounts()
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0");
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        using var models = await client.GetAsync("/v1/models");
        using var mutation = await client.PostAsJsonAsync("/v1/gateway/accounts", new { id = "unauthorized", executable = "arbitrary.exe" });
        Assert.Equal(HttpStatusCode.Unauthorized, models.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
        Assert.DoesNotContain(await gateway.GetAccountsAsync(), account => account.Id == "unauthorized");
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Origin", "null")]
    [InlineData("Host", "evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Sec-Fetch-Mode", "navigate")]
    public async Task DefaultServerRejectsBrowserRequestsEvenWithKey(string header, string value)
    {
        using var directory = new TestDirectory();
        using var gateway = Create(directory, new Adapter());
        await using var server = await EmbeddedGatewayServer.StartAsync(gateway, "http://127.0.0.1:0", new() { ApiKey = "fixture" });
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture");
        client.DefaultRequestHeaders.TryAddWithoutValidation(header, value);
        using var response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

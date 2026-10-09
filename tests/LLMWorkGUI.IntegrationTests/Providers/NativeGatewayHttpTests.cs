using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class NativeGatewayHttpTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    public void Dispose()
    {
        TestSqlitePool.Clear(new SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        _directory.Dispose();
    }

    [Fact]
    public async Task DefaultStartupDoesNotListenOrConstructNativeGateway()
    {
        using var host = Build(new Catalog(), new Turns());
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);
        Assert.Null(host.Services.GetRequiredService<NativeGatewayHttpServer>().Url);
        Assert.False(File.Exists(_directory.GetPath("llmgateway/accounts.json")));
        await host.StopAsync();
    }

    [Fact]
    public async Task RealLoopbackRequiresDedicatedKeyAndHonorsRotationRevocationAndBrowserGuards()
    {
        var catalog = new Catalog(); var turns = new Turns();
        using var host = Build(catalog, turns);
        await host.StartAsync(); await HostBootstrapper.InitializeAsync(host);
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var key = new string('a', 48); // Synthetic fixture, never a native account credential.
        var reference = await lifecycle.CreateAsync(key, SecretReferenceKind.GatewayApiKey);
        try
        {
            var options = Configure(host, reference.Reference);
            var server = host.Services.GetRequiredService<NativeGatewayHttpServer>();
            await server.StartEnabledAsync(); await server.StartEnabledAsync();
            using var client = new HttpClient { BaseAddress = new Uri(server.Url!), Timeout = TimeSpan.FromSeconds(15) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models/route")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/models/ROUTE")).StatusCode);
            Assert.Equal((options.ProjectId!, LLMWorkGUI.Domain.Entities.ProjectLock.CanonicalizeRoot(options.RootPath!)), catalog.Last);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/gateway/accounts")).StatusCode);
            client.DefaultRequestHeaders.Add("Origin", "https://example.test");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Host = "example.test";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            client.DefaultRequestHeaders.Host = null;
            var rotated = new string('b', 48);
            await lifecycle.RotateAsync(reference.Reference, rotated);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            await lifecycle.RevokeAsync(reference.Reference);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            Assert.Null(turns.Last);
        }
        finally { await lifecycle.RevokeAsync(reference.Reference); await host.StopAsync(); }
    }

    [Theory]
    [InlineData(SecretReferenceKind.ProviderApiKey, 48)]
    [InlineData(SecretReferenceKind.GatewayApiKey, 8)]
    public async Task RefusesWrongPurposeOrShortKeyWithoutOpeningListener(SecretReferenceKind kind, int length)
    {
        using var host = Build(new Catalog(), new Turns());
        await HostBootstrapper.InitializeAsync(host);
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var reference = await lifecycle.CreateAsync(new string('x', length), kind);
        try
        {
            Configure(host, reference.Reference);
            var server = host.Services.GetRequiredService<NativeGatewayHttpServer>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartEnabledAsync());
            Assert.Null(server.Url);
        }
        finally { await lifecycle.RevokeAsync(reference.Reference); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpDispatchBindsProjectRouteAndRefusesAmbiguousResult(bool stream)
    {
        var catalog = new Catalog(); var turns = new Turns();
        using var host = Build(catalog, turns);
        await HostBootstrapper.InitializeAsync(host);
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var key = new string('c', 48);
        var reference = await lifecycle.CreateAsync(key, SecretReferenceKind.GatewayApiKey);
        try
        {
            Configure(host, reference.Reference);
            var server = host.Services.GetRequiredService<NativeGatewayHttpServer>(); await server.StartEnabledAsync();
            using var client = new HttpClient { BaseAddress = new Uri(server.Url!), Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var body = new { model = "route", stream, messages = new[] { new { role = "user", content = "fixture prompt" } } };
            using var response = await client.PostAsJsonAsync("/v1/chat/completions", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("fixture answer", content);
            Assert.False(response.Headers.Contains("X-LLM-Account"));
            Assert.DoesNotContain("x_gateway", content);
            Assert.DoesNotContain("native-model", content);
            Assert.Equal("project", turns.Last!.ProjectId);
            Assert.Equal(LLMWorkGUI.Domain.Entities.ProjectLock.CanonicalizeRoot(_directory.Root), turns.Last.RootPath);
            Assert.Equal(catalog.Route.Binding, turns.Last.ExpectedBinding);
            Assert.Equal("fixture prompt", turns.Last.Prompt);
            turns.Result = turns.Result with { State = ExecutionState.Ambiguous, RequiresReconciliation = true };
            using var refused = await client.PostAsJsonAsync("/v1/chat/completions", body);
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.DoesNotContain("fixture answer", await refused.Content.ReadAsStringAsync());
        }
        finally { await lifecycle.RevokeAsync(reference.Reference); await host.StopAsync(); }
    }

    [Fact]
    public async Task FacadeRejectsUnknownRouteAccountAndUnsupportedConversationBeforeDispatch()
    {
        var catalog = new Catalog(); var turns = new Turns();
        var facade = new ProjectNativeGateway(catalog, turns, "project", _directory.Root, TimeSpan.FromSeconds(10));
        foreach (var request in new[]
        {
            new ChatRequest { Model = "auto", Messages = [ChatMessage.User("fixture")] },
            new ChatRequest { Model = "route", AccountId = "foreign", Messages = [ChatMessage.User("fixture")] },
            new ChatRequest { Model = "route", Messages = [ChatMessage.System("system"), ChatMessage.User("fixture")] },
            new ChatRequest { Model = "route", Messages = [ChatMessage.User("fixture")], ReasoningEffort = "high" }
        }) await Assert.ThrowsAsync<GatewayException>(() => facade.CompleteAsync(request));
        Assert.Null(turns.Last);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("none")]
    public async Task ExplicitToolChoiceIsNotSilentlyDiscardedBeforeFacadeValidation(string choice)
    {
        var turns = new Turns();
        var dto = JsonSerializer.Deserialize<OpenAiChatRequest>(
            "{\"model\":\"route\",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}],\"tool_choice\":\"" + choice + "\"}", GatewayJson.Options)!;
        var request = OpenAiMapper.ToChatRequest(dto);
        var facade = new ProjectNativeGateway(new Catalog(), turns, "project", _directory.Root, TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<GatewayException>(() => facade.CompleteAsync(request));
        Assert.Null(turns.Last);
    }

    [Theory]
    [InlineData("\"top_p\":0.1")]
    [InlineData("\"seed\":1")]
    [InlineData("\"presence_penalty\":1")]
    [InlineData("\"frequency_penalty\":1")]
    [InlineData("\"suffix\":\"additional constraint\"")]
    public void LegacyCompletionConstraintsAreRejectedInsteadOfDropped(string field)
    {
        var dto = JsonSerializer.Deserialize<OpenAiCompletionRequest>(
            "{\"model\":\"route\",\"prompt\":\"fixture\"," + field + "}", GatewayJson.Options)!;
        Assert.Throws<GatewayException>(() => OpenAiMapper.ToChatRequest(dto));
    }

    private IHost Build(Catalog catalog, Turns turns) => HostBootstrapper.CreateHostBuilder(appDataDirectory: _directory.Root)
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<INativeGatewayRouteCatalog>(catalog);
            services.AddSingleton<INativeGatewayTurnService>(turns);
            services.AddSingleton<ILlmGateway>(_ => throw new InvalidOperationException("Native gateway must stay lazy"));
        }).Build();

    private NativeGatewayHttpOptions Configure(IHost host, string reference)
    {
        var options = host.Services.GetRequiredService<IOptions<NativeGatewayHttpOptions>>().Value;
        options.Enabled = true; options.Port = 0; options.ProjectId = "project";
        options.RootPath = _directory.Root; options.ApiKeySecretReference = reference;
        return options;
    }

    private sealed class Catalog : INativeGatewayRouteCatalog
    {
        public NativeGatewayRouteOption Route { get; } = new("route", new(GatewayCatalogMapper.ProviderId(ProviderKind.Codex),
            "account", "model", "native-account", "native-model"), "Provider", "Account", "Model");
        public (string, string)? Last { get; private set; }
        public Task<IReadOnlyList<NativeGatewayRouteOption>> ListAsync(string projectId, string rootPath, CancellationToken cancellationToken = default)
        { Last = (projectId, rootPath); return Task.FromResult<IReadOnlyList<NativeGatewayRouteOption>>([Route]); }
    }

    private sealed class Turns : INativeGatewayTurnService
    {
        public NativeGatewayTurnRequest? Last { get; private set; }
        public NativeGatewayTurnResult Result { get; set; } = new("session", "execution", ExecutionState.Succeeded,
            ExecutionFailureReason.None, "fixture answer", false);
        public Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken cancellationToken = default)
        { Last = request; return Task.FromResult(Result); }
    }
}

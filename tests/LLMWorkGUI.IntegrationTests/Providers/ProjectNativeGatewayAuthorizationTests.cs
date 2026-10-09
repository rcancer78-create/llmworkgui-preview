using System.Text.Json;
using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class ProjectNativeGatewayAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacadeRefusesLocalAuthorizationBeforeReadingCatalogOrDispatch(bool stream)
    {
        var catalog = new Catalog(); var turns = new Turns(); var authorization = new Authorization();
        var facade = new ProjectNativeGateway(catalog, turns, "project", Path.GetTempPath(), TimeSpan.FromSeconds(10));
        var request = new ChatRequest { Model = "route", Messages = [ChatMessage.User("fixture")], DispatchAuthorization = authorization };
        var error = await Assert.ThrowsAsync<GatewayException>(async () =>
        {
            if (stream) { await foreach (var _ in facade.StreamAsync(request)) { } }
            else await facade.CompleteAsync(request);
        });
        Assert.Equal(GatewayErrorKind.Unsupported, error.Kind);
        Assert.Equal(0, catalog.Calls); Assert.Equal(0, turns.Calls); Assert.Equal(0, authorization.Calls);
    }

    [Fact]
    public void JsonCannotExportOrImportLocalAuthorization()
    {
        var request = new ChatRequest { Model = "route", Messages = [ChatMessage.User("fixture")], DispatchAuthorization = new Authorization() };
        var json = JsonSerializer.Serialize(request, GatewayJson.Options);
        Assert.DoesNotContain("dispatchAuthorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExpiresAtUtc", json, StringComparison.OrdinalIgnoreCase);
        var injected = JsonSerializer.Deserialize<ChatRequest>("{\"model\":\"route\",\"dispatchAuthorization\":{\"expiresAtUtc\":\"2099-01-01T00:00:00Z\"}}", GatewayJson.Options);
        Assert.NotNull(injected); Assert.Null(injected.DispatchAuthorization);
    }

    private sealed class Catalog : INativeGatewayRouteCatalog
    {
        public int Calls;
        public Task<IReadOnlyList<NativeGatewayRouteOption>> ListAsync(string project, string root, CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<NativeGatewayRouteOption>>([new("route", new(GatewayCatalogMapper.ProviderId(ProviderKind.Codex),
                "account", "model", "native-account", "native-model"), "Provider", "Account", "Model")]);
        }
    }
    private sealed class Turns : INativeGatewayTurnService
    {
        public int Calls;
        public Task<NativeGatewayTurnResult> ExecuteAsync(NativeGatewayTurnRequest request, CancellationToken token = default)
        {
            Calls++;
            return Task.FromResult(new NativeGatewayTurnResult("session", "execution", ExecutionState.Succeeded, ExecutionFailureReason.None, "fixture answer", false));
        }
    }
    private sealed class Authorization : INativeDispatchAuthorization
    {
        public int Calls;
        public DateTimeOffset ExpiresAtUtc => throw new InvalidOperationException("Local authority must not be serialized");
        public Task ValidatePreparedAsync(AccountProfile account, NativeChatRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Facade must not use this authority"); }
        public Task AuthorizeTransportAsync(AccountProfile account, NativeChatRequest request, string wire, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Facade must not use this authority"); }
    }
}

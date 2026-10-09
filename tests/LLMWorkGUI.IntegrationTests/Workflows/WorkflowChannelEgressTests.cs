using System.Net;
using System.Text;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>Stored SQLite route and real client/owned HTTP handler; no live gateway/native model.</summary>
public sealed class WorkflowChannelEgressTests
{
    [Fact]
    public async Task DirectChannelCannotSendProjectPromptWithoutStoredExecutionAdmission()
    {
        using var db = new TestDatabase(); await db.InitializeAsync(); await db.SeedRouteChainAsync();
        await using (var connection = await db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ProviderProfiles SET Backend='StarCliProxy',BaseUrl='http://127.0.0.1:4971/'; UPDATE Routes SET Backend='StarCliProxy'; UPDATE Models SET Backend='StarCliProxy'";
            await command.ExecuteNonQueryAsync();
        }
        var handler = new Handler(); using var http = new HttpClient(handler);
        var channel = new StarCliProxyReviewReadOnlyChannel(new SqliteRouteRepository(db.Factory), new Locator(), new StarCliProxyClient(http));
        var request = new WorkflowChannelTurnRequest("caller-execution",
            new WorkflowNodeDefinition("stage", WorkflowNodeKind.Review, "Review", "Reviewer"),
            new RoleBindingDefinition("Reviewer", "route-1", modelId: "model-1"), "route-1", true, "synthetic project review text");
        var result = await channel.RunAsync(request);
        Assert.Equal(0, handler.Count);
        Assert.Equal(ExecutionState.Failed, result.State); Assert.Null(result.ObservedRouteId);
    }
    private sealed class Locator : IReviewChannelGatewayLocator
    {
        public ReviewChannelGatewayLocation Locate(LLMWorkGUI.Application.Repositories.WorkflowRouteAssignment assignment)
            => ReviewChannelGatewayLocation.At(StarCliProxyEndpoint.Loopback(4971, "owned-synthetic-key"));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}",Encoding.UTF8,"application/json") });
        }
    }
}

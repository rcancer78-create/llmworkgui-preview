using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

public sealed class JsonRpcStdioTransportIntegrationTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CursorAcpClient_InitializeAsync_OverStdioPipes_ProducesFixtureEvidence()
    {
        await using var harness = new CursorAcpPipeHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var id = document.RootElement.GetProperty("id").GetInt32();

                return CursorAcpIntegrationTestData.CreateResponseJson(
                    id,
                    CursorAcpIntegrationTestData.ReadHandshakeResult());
            }
        };

        var factory = new JsonRpcStdioTransportFactory();
        await using var transport = factory.Create(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions { HandshakeTimeout = BoundedWait });

        var result = await client.InitializeAsync();

        Assert.True(result.IsReady);
        Assert.NotNull(result.Evidence);
        Assert.Equal(1, result.Evidence!.ProtocolVersion);
        Assert.True(result.Evidence.AgentCapabilities.LoadSession);
        Assert.True(result.Evidence.AgentCapabilities.SessionList);
        Assert.Equal("cursor_login", Assert.Single(result.Evidence.AuthMethods).Id);

        var requestFrame = await harness.ReadFrameAsync(BoundedWait);
        using var requestDocument = JsonDocument.Parse(requestFrame);
        Assert.Equal(JsonValueKind.Number, requestDocument.RootElement
            .GetProperty("params").GetProperty("protocolVersion").ValueKind);
    }

    [Fact]
    public async Task CursorAcpClient_InitializeAsync_MalformedFrameThenResponse_StillReachesReady()
    {
        await using var harness = new CursorAcpPipeHarness();
        var factory = new JsonRpcStdioTransportFactory();
        await using var transport = factory.Create(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions { HandshakeTimeout = BoundedWait });

        var initializeTask = client.InitializeAsync();
        var frame = await harness.ReadFrameAsync(BoundedWait);

        using var document = JsonDocument.Parse(frame);
        var id = document.RootElement.GetProperty("id").GetInt32();

        await harness.WriteRawLineAsync("garbage that is not json");
        await harness.WriteRawLineAsync(
            CursorAcpIntegrationTestData.CreateResponseJson(
                id,
                CursorAcpIntegrationTestData.ReadHandshakeResult()));

        var result = await initializeTask;

        Assert.True(result.IsReady);
        Assert.Equal(1, transport.MalformedFrameCount);
    }

    [Fact]
    public async Task CursorAcpClient_InitializeAsync_NoAgentResponse_DegradesWithHandshakeTimeout()
    {
        await using var harness = new CursorAcpPipeHarness();
        var factory = new JsonRpcStdioTransportFactory();
        await using var transport = factory.Create(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });

        var result = await client.InitializeAsync();

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpHandshakeFailureKind.HandshakeTimeout, result.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
    }
}

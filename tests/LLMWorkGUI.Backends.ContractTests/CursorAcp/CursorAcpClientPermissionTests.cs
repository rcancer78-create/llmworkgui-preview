using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientPermissionTests
{
    [Theory]
    [InlineData(CursorAcpPermissionDecision.AllowOnce)]
    [InlineData(CursorAcpPermissionDecision.Deny)]
    public async Task ReplyPermissionAsync_UnsolicitedRawIdNeverWritesAResponse(CursorAcpPermissionDecision decision)
    {
        var (client, transport) = await CreateReadyClientAsync();
        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
            { PermissionId = "unsolicited-id", SessionId = "sess-test-42", Decision = decision });
        Assert.False(result.IsSent);
        Assert.Equal(CursorAcpPermissionReplyFailureKind.InvalidPermissionId, result.FailureKind);
        Assert.Empty(transport.Responses);
    }

    [Theory]
    [InlineData(CursorAcpPermissionDecision.AllowOnce)]
    [InlineData(CursorAcpPermissionDecision.Deny)]
    public async Task RawEnvelopeIdCannotBypassCapturedSessionReceipt(CursorAcpPermissionDecision decision)
    {
        var (client, transport) = await CreateReadyClientAsync();
        var permission = await CaptureLegacyPermissionAsync(client, transport, "captured-wire-id");
        var forged = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
            { PermissionId = "captured-wire-id", SessionId = "different-session", Decision = decision });
        Assert.False(forged.IsSent);
        Assert.Empty(transport.Responses);

        var correct = new CursorAcpPermissionReplyRequest
            { PermissionId = permission.RequestId, SessionId = permission.SessionId, Decision = decision };
        Assert.True((await client.ReplyPermissionAsync(correct)).IsSent);
        Assert.Equal("captured-wire-id", Assert.Single(transport.Responses).Id.GetString());
        Assert.False((await client.ReplyPermissionAsync(correct)).IsSent);
        Assert.Single(transport.Responses);
    }

    [Fact]
    public void PermissionRequest_DoesNotAdvertiseAllowWithoutDiscoveredOneShotOption()
    {
        Assert.False(new CursorAcpStreamEvent.PermissionRequest
            { Method = "session/request_permission", RequestId = "synthetic", Description = "No native option evidence" }.CanAllowOnce);
    }

    [Theory]
    [InlineData("23")]
    [InlineData("\"rpc-envelope-id\"")]
    public async Task LegacyPermissionReplyUsesExactEnvelopeIdInsteadOfPayloadAlias(string rpcId)
    {
        var (client, transport) = await CreateReadyClientAsync();
        using var id = JsonDocument.Parse(rpcId);
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission",
            Id = id.RootElement.Clone(),
            Parameters = JsonSerializer.SerializeToElement(new
            {
                sessionId = "sess-test-42", permissionId = "different-payload-id", description = "Synthetic request"
            })
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = client.SubscribeEventsAsync(cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var permission = Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events.Current);

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId, SessionId = permission.SessionId,
            Decision = CursorAcpPermissionDecision.Deny
        });

        Assert.True(result.IsSent);
        Assert.Equal(rpcId, Assert.Single(transport.Responses).Id.GetRawText());
    }

    [Fact]
    public async Task ReplyPermissionAsync_AllowOnce_SendsFixtureResponseFrame()
    {
        var (client, transport) = await CreateReadyClientAsync();
        var permission = await CaptureLegacyPermissionAsync(client, transport, "perm-req-1");
        Assert.True(permission.CanAllowOnce);

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId,
            Decision = CursorAcpPermissionDecision.AllowOnce,
            SessionId = permission.SessionId
        });

        Assert.True(result.IsSent);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);

        var response = Assert.Single(transport.Responses);
        Assert.Equal("perm-req-1", response.Id.GetString());
        Assert.Null(response.Error);
        Assert.Equal("allow_once", response.Result!.Value.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ReplyPermissionAsync_Deny_SendsDenyDecision()
    {
        var (client, transport) = await CreateReadyClientAsync();
        var permission = await CaptureLegacyPermissionAsync(client, transport, "perm-req-2");

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId,
            SessionId = permission.SessionId,
            Decision = CursorAcpPermissionDecision.Deny
        });

        Assert.True(result.IsSent);

        var response = Assert.Single(transport.Responses);
        Assert.Equal("deny", response.Result!.Value.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ReplyPermissionAsync_BeforeReadyHandshake_IsDegradedWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = "perm-req-1",
            Decision = CursorAcpPermissionDecision.AllowOnce
        });

        Assert.False(result.IsSent);
        Assert.Equal(CursorAcpPermissionReplyFailureKind.NotReady, result.FailureKind);
        Assert.Empty(transport.Responses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReplyPermissionAsync_EmptyPermissionId_IsInvalidPermissionId(string permissionId)
    {
        var (client, transport) = await CreateReadyClientAsync();

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permissionId,
            Decision = CursorAcpPermissionDecision.Deny
        });

        Assert.Equal(CursorAcpPermissionReplyFailureKind.InvalidPermissionId, result.FailureKind);
        Assert.Empty(transport.Responses);
    }

    [Fact]
    public async Task ReplyPermissionAsync_TransportFailure_IsDegradedTransportFailure()
    {
        var transport = new FakeJsonRpcTransport((method, _, _, _) => method == CursorAcpClient.InitializeMethod
            ? Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()))
            : Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(JsonSerializer.SerializeToElement(new { }))));
        var client = CreateClient(transport);
        await client.InitializeAsync();
        var permission = await CaptureLegacyPermissionAsync(client, transport, "perm-req-1");

        transport.FailResponseWrites(new JsonRpcTransportException(
            JsonRpcTransportFailureKind.WriteFailed,
            "broken pipe"));

        var result = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId,
            SessionId = permission.SessionId,
            Decision = CursorAcpPermissionDecision.AllowOnce
        });

        Assert.Equal(CursorAcpPermissionReplyFailureKind.TransportFailure, result.FailureKind);
        Assert.Contains("broken pipe", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionDecision_ExposesOnlyOneShotAllowAndDeny()
    {
        Assert.Equal(
            new[] { "AllowOnce", "Deny" },
            Enum.GetNames<CursorAcpPermissionDecision>());
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static async Task<CursorAcpStreamEvent.PermissionRequest> CaptureLegacyPermissionAsync(
        CursorAcpClient client, FakeJsonRpcTransport transport, string wireId)
    {
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(wireId),
            Parameters = JsonSerializer.SerializeToElement(new { sessionId = "sess-test-42", description = "Synthetic legacy request" })
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = client.SubscribeEventsAsync(cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        return Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events.Current);
    }

    private static async Task<(CursorAcpClient Client, FakeJsonRpcTransport Transport)> CreateReadyClientAsync()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw new InvalidOperationException($"Unexpected ACP method '{method}'."));

        var client = CreateClient(transport);
        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady);

        return (client, transport);
    }
}

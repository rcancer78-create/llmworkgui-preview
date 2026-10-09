using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpNativeModeTests
{
    [Theory]
    [InlineData("ask", CursorAcpMode.Ask)]
    [InlineData("plan", CursorAcpMode.Plan)]
    public async Task SupervisedReadOnlyTurn_SelectsNativeModeBeforePrompt(string modeId, CursorAcpMode mode)
    {
        var transport = CreateTransport(_ => Reply("{}"));
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var policy = new CursorAcpModePolicy();
        var supervisor = new CursorAcpTurnSupervisor(client, policy);

        var result = await supervisor.ExecuteTurnAsync(
            policy.Evaluate(mode, CursorAcpModeAccess.ReadOnly, CapabilityState.Supported) with { ModeId = "agent" },
            null,
            Request("agent"));

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "initialize", "session/set_mode", "session/prompt" }, transport.Requests.Select(r => r.Method));
        var selection = transport.Requests[1].Parameters!.Value;
        Assert.Equal(modeId, selection.GetProperty("modeId").GetString());
        Assert.Equal("session-a", selection.GetProperty("sessionId").GetString());
        Assert.False(transport.Requests[2].Parameters!.Value.TryGetProperty("modeId", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"currentModeId\":\"agent\"}")]
    [InlineData("{\"modeId\":null}")]
    [InlineData("{\"modeId\":42}")]
    [InlineData("{\"modeId\":\"ask\",\"modeId\":\"agent\"}")]
    [InlineData("{\"modeId\":\"agent\",\"modeId\":\"ask\"}")]
    public async Task InvalidAcknowledgement_NeverDispatchesPrompt(string payload)
    {
        var transport = CreateTransport(_ => Reply(payload));
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var result = await client.PromptAsync(Request("ask"));
        Assert.Equal(CursorAcpPromptFailureKind.ModeSelectionFailed, result.FailureKind);
        Assert.DoesNotContain(transport.Requests, r => r.Method == "session/prompt");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("transport")]
    [InlineData("error")]
    [InlineData("missing")]
    public async Task SelectionFailure_NeverDispatchesOrRetries(string failure)
    {
        var transport = CreateTransport(_ => failure switch
        {
            "timeout" => throw new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timeout"),
            "transport" => throw new IOException("closed"),
            "error" => new JsonRpcResponse { Id = JsonSerializer.SerializeToElement(2), Error = new JsonRpcError { Code = -32601, Message = "Unsupported" } },
            _ => new JsonRpcResponse { Id = JsonSerializer.SerializeToElement(2) }
        });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var policy = new CursorAcpModePolicy();
        var supervisor = new CursorAcpTurnSupervisor(client, policy);
        var result = await supervisor.ExecuteTurnAsync(policy.Evaluate(CursorAcpMode.Ask, CursorAcpModeAccess.ReadOnly, CapabilityState.Supported), null, Request("ask"));
        Assert.Equal(CursorAcpTurnOutcome.Failed, result.Outcome);
        Assert.Equal(new[] { "initialize", "session/set_mode" }, transport.Requests.Select(r => r.Method));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ASK")]
    [InlineData("unknown")]
    public async Task InvalidMode_NeverSendsSelectionOrPrompt(string mode)
    {
        var transport = CreateTransport(_ => Reply("{}"));
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        Assert.Equal(CursorAcpPromptFailureKind.ModeSelectionFailed, (await client.PromptAsync(Request(mode))).FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task CancellationDuringAcknowledgement_DoesNotDispatchLatePrompt_AndReleasesGate()
    {
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        var transport = CreateTransport(_ => { if (++attempts == 1) cancel.Cancel(); return Reply("{}"); });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PromptAsync(Request("ask"), cancel.Token));
        Assert.DoesNotContain(transport.Requests, r => r.Method == "session/prompt");
        Assert.True((await client.PromptAsync(Request("plan"))).IsSuccess);
        Assert.Single(transport.Requests.Where(r => r.Method == "session/prompt"));
    }

    [Fact]
    public async Task ConcurrentProtocolCalls_CannotChangeModeDuringFirstPrompt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var transport = new FakeJsonRpcTransport(async (method, parameters, _, token) =>
        {
            methods.Enqueue(method == "session/set_mode" ? parameters!.Value.GetProperty("modeId").GetString()! : method);
            if (method == "initialize") return FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult());
            if (method == "session/prompt") { entered.TrySetResult(); await release.Task.WaitAsync(token); return Reply("{\"stopReason\":\"end_turn\"}"); }
            return Reply("{}");
        });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var first = client.PromptAsync(Request("ask"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = client.PromptAsync(Request("agent"));
        Assert.Equal(new[] { "initialize", "ask", "session/prompt" }, methods.ToArray());
        Assert.Equal(CursorAcpPromptFailureKind.PromptBusy, (await waiting).FailureKind);
        release.SetResult();
        Assert.True((await first).IsSuccess);
        Assert.Equal(new[] { "initialize", "ask", "session/prompt" }, methods.ToArray());
    }

    [Fact]
    public async Task SupervisedCallerCancellation_DuringModePreparation_NeverDispatchesLatePrompt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeJsonRpcTransport(async (method, _, _, _) =>
        {
            if (method == "initialize") return FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult());
            if (method == "session/set_mode") { entered.TrySetResult(); await release.Task; return Reply("{}"); }
            throw new InvalidOperationException("Late prompt dispatched.");
        });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var policy = new CursorAcpModePolicy();
        var supervisor = new CursorAcpTurnSupervisor(client, policy);
        using var cancel = new CancellationTokenSource();
        var task = supervisor.ExecuteTurnAsync(policy.Evaluate(CursorAcpMode.Ask, CursorAcpModeAccess.ReadOnly, CapabilityState.Supported), null, Request("ask"), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        release.SetResult();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CursorAcpTurnOutcome.Cancelled, result.Outcome);
        Assert.DoesNotContain(transport.Requests, r => r.Method == "session/prompt");
        Assert.Empty(transport.SentNotifications);
    }

    [Fact]
    public async Task ClientAlreadyBusy_SecondSupervisedTurnFailsWithoutHoldingItsWriterLock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeJsonRpcTransport(async (method, _, _, token) =>
        {
            if (method == "initialize") return FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult());
            if (method == "session/prompt") { entered.TrySetResult(); await release.Task.WaitAsync(token); return Reply("{\"stopReason\":\"end_turn\"}"); }
            return Reply("{}");
        });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var first = client.PromptAsync(Request("ask"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var policy = new CursorAcpModePolicy();
        var held = new FakeCheckoutLockToken();
        var health = new RecordingHealthCenterService();
        await using var lifecycle = new CursorAcpSessionLifecycleService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(transport)),
            new StubCursorAcpClientFactory(client), policy, healthCenter: health);
        Assert.True((await lifecycle.StartBackendAsync("busy-health-regression")).IsReady);
        try
        {
            var result = await lifecycle.ExecuteTurnAsync(new CursorAcpTurnRequest {
                Mode = policy.Evaluate(CursorAcpMode.Agent, CursorAcpModeAccess.Write, CapabilityState.Supported),
                LockToken = held, Prompt = Request("agent") with { SessionId = "session-b" } });
            Assert.Equal(CursorAcpTurnOutcome.Failed, result.Outcome);
            Assert.Equal(CursorAcpTurnFailureKind.ConcurrentTurn, result.FailureKind);
            Assert.True(result.LockReleased);
            Assert.False(result.LockRetained);
            Assert.Empty(health.Failures);
            Assert.Single(transport.Requests.Where(r => r.Method == "session/set_mode"));
        }
        finally { release.TrySetResult(); }
        await first;
    }

    private static CursorAcpPromptRequest Request(string mode) => CursorAcpPromptRequest.Create("session-a", "Return ACP_OK without tools.") with { ModeId = mode };
    private static JsonRpcResponse Reply(string payload) => FakeJsonRpcTransport.CreateResultResponse(JsonDocument.Parse(payload).RootElement.Clone());
    private static FakeJsonRpcTransport CreateTransport(Func<JsonElement?, JsonRpcResponse> mode) => new((method, parameters, _, _) => Task.FromResult(method switch
    {
        "initialize" => FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()),
        "session/set_mode" => mode(parameters),
        "session/prompt" => Reply("{\"stopReason\":\"end_turn\"}"),
        _ => throw new InvalidOperationException(method)
    }));
}

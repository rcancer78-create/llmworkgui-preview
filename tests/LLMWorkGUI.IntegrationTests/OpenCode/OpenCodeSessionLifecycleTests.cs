using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeSessionLifecycleTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PromptHttpFailureRetainsDeliveryUncertaintyAndPreventsAnotherSend(HttpStatusCode status)
    {
        var promptCalls = 0;
        using var http = CreateHttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_full")));
            if (request.RequestUri.AbsolutePath == "/event")
                return Task.FromResult(SseResponse(ServerConnected));
            if (request.RequestUri.AbsolutePath == "/session/ses_full/prompt_async")
            {
                Interlocked.Increment(ref promptCalls);
                return Task.FromResult(StubHttpMessageHandler.Json("{\"error\":\"synthetic native failure\"}", status));
            }
            throw new InvalidOperationException("Unexpected synthetic endpoint.");
        }));
        var lifecycle = CreateService(http);
        await lifecycle.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest());
        var result = await lifecycle.ExecuteTurnAsync("ses_full",
            new OpenCodePromptRequest { Prompt = "synthetic request", MessageId = "msg_http_failure" });
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.True(result.IsDeliveryUncertain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ExecuteTurnAsync("ses_full",
            new OpenCodePromptRequest { Prompt = "another request", MessageId = "msg_after_failure" }));
        Assert.Equal(1, promptCalls);
    }

    private static readonly Uri BaseUrl = new("http://127.0.0.1:54321");

    private const string ServerConnected = """{"type":"server.connected","properties":{}}""";

    private const string TextPartHello =
        """{"type":"message.part.updated","properties":{"part":{"id":"prt_text_1","sessionID":"ses_full","messageID":"msg_1","type":"text","text":"Hello "}}}""";

    private const string TextPartWorld =
        """{"type":"message.part.updated","properties":{"part":{"id":"prt_text_2","sessionID":"ses_full","messageID":"msg_1","type":"text","text":"world"}}}""";

    private const string ToolPart =
        """{"type":"message.part.updated","properties":{"part":{"id":"prt_tool_1","sessionID":"ses_full","messageID":"msg_1","type":"tool","callID":"call_1","tool":"read","state":{"status":"completed","input":{"filePath":"C:\\workspace\\demo-app\\README.md"},"output":"ok"}}}}""";

    private const string StepFinish =
        """{"type":"message.part.updated","properties":{"part":{"id":"prt_step_1","sessionID":"ses_full","messageID":"msg_1","type":"step-finish","reason":"stop","tokens":{"input":1234,"output":567,"reasoning":123,"cache":{"read":800,"write":200}},"cost":0}}}""";

    private const string SessionIdleFull =
        """{"type":"session.idle","properties":{"sessionID":"ses_full"}}""";

    [Theory]
    [InlineData("idle")]
    [InlineData("old-parent")]
    [InlineData("missing-parent")]
    [InlineData("unknown-message")]
    public async Task ExecuteTurnAsync_StaleOrUnboundTerminalCannotCompletePrompt(string vector)
    {
        const string current = "msg_current_request";
        var info = JsonSerializer.Serialize(new { type = "message.updated", properties = new { info = new
        {
            id = "msg_1", sessionID = "ses_full", role = "assistant",
            parentID = vector == "old-parent" ? "msg_old_request" : (string?)null
        } } });
        var events = vector == "idle" ? new[] { ServerConnected, SessionIdleFull }
            : vector == "unknown-message" ? new[] { ServerConnected, TextPartHello, StepFinish }
            : new[] { ServerConnected, info, TextPartHello, StepFinish, SessionIdleFull };
        using var http = CreateHttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/session" => StubHttpMessageHandler.Json(SessionJson("ses_full")),
                "/event" => SseResponse(events),
                _ => StubHttpMessageHandler.Json("true")
            })));
        var service = CreateService(http);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest());
        var result = await service.ExecuteTurnAsync("ses_full", new OpenCodePromptRequest { Prompt = "new request", MessageId = current });
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.True(result.IsDeliveryUncertain);
        Assert.Empty(result.OutputText);
    }

    [Fact]
    public async Task ExecuteTurnAsync_SkipsOldTerminalAndRejectsReusedPromptIdentity()
    {
        using var http = CreateHttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/session" => StubHttpMessageHandler.Json(SessionJson("ses_full")),
                "/event" => SseResponse(ServerConnected, SessionIdleFull, StepFinish,
                    BoundAssistant("ses_full"), TextPartHello, StepFinish),
                _ => StubHttpMessageHandler.Json("true")
            })));
        var service = CreateService(http);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest());
        var prompt = new OpenCodePromptRequest { Prompt = "new request", MessageId = "msg_request" };
        var result = await service.ExecuteTurnAsync("ses_full", prompt);
        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("Hello ", result.OutputText);
        Assert.False(result.IsDeliveryUncertain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTurnAsync("ses_full", prompt));
    }

    [Fact]
    public async Task ExecuteTurnAsync_KeepsTheModelReportedByTheAssistantMessage()
    {
        using var http = CreateHttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/session" => StubHttpMessageHandler.Json(SessionJson("ses_full")),
                "/event" => SseResponse(ServerConnected, BoundAssistant("ses_full", modelId: "provider/native-model"), TextPartHello, StepFinish),
                _ => StubHttpMessageHandler.Json("true")
            })));
        var service = CreateService(http);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest());
        var result = await service.ExecuteTurnAsync("ses_full", new OpenCodePromptRequest { Prompt = "new request", MessageId = "msg_request" });
        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("provider/native-model", result.ObservedModelId);
        Assert.Equal("opencode", result.ObservedProviderId);
    }

    [Theory]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"p\"", true)]
    [InlineData("\"modelID\":\"m\"", true)]
    [InlineData("\"providerID\":\"p\"", true)]
    [InlineData("\"providerID\":\"other\"", false)]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"other\"", false)]
    [InlineData("\"modelID\":\"m\",\"providerID\":\"p\",\"model\":{\"providerID\":\"other\"}", false)]
    public async Task ObservedProviderMustRemainConsistentAcrossNativeUpdates(string lastFields, bool known)
    {
        string Message(string fields) => "{\"type\":\"message.updated\",\"properties\":{\"info\":{\"id\":\"msg_1\",\"sessionID\":\"ses_full\",\"role\":\"assistant\",\"parentID\":\"msg_request\"," + fields + "}}}";
        using var http = CreateHttpClient(new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/session" => StubHttpMessageHandler.Json(SessionJson("ses_full")),
                "/event" => SseResponse(ServerConnected, Message("\"modelID\":\"m\",\"providerID\":\"p\""), Message(lastFields), TextPartHello, StepFinish),
                _ => StubHttpMessageHandler.Json("true")
            })));
        var service = CreateService(http);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest());
        var result = await service.ExecuteTurnAsync("ses_full", new OpenCodePromptRequest { Prompt = "new request", MessageId = "msg_request" });
        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal(known ? "m" : null, result.ObservedModelId);
        Assert.Equal(known ? "p" : null, result.ObservedProviderId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelTurnAsync_WithoutOwnedTurn_DoesNotAbortOrClaimTerminalConfirmation(bool knownIdleSession)
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == OpenCodeApiPaths.Sessions
                ? StubHttpMessageHandler.Json(SessionJson("ses_idle"))
                : StubHttpMessageHandler.Json("true")));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        if (knownIdleSession)
        {
            await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest
            {
                Directory = @"D:\synthetic-workspace", Model = "test-provider/test-model"
            });
        }

        var confirmed = await service.CancelTurnAsync(knownIdleSession ? "ses_idle" : "ses_foreign");

        Assert.False(confirmed);
        Assert.DoesNotContain(handler.Requests, request => request.Path.EndsWith("/abort", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteTurnAsync_CompletesTurnWithTextToolCallsAndUsage()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_full")));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_full/prompt_async")
            {
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(SseResponse(
                    ServerConnected,
                    BoundAssistant("ses_full"),
                    TextPartHello,
                    ToolPart,
                    TextPartWorld,
                    StepFinish,
                    SessionIdleFull));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var session = await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest
        {
            Directory = @"C:\workspace\demo-app",
            Title = "Demo session",
            Model = "test-provider/test-model",
            Agent = "build"
        });

        Assert.Equal("ses_full", session.Id);

        var result = await service.ExecuteTurnAsync(
            session.Id,
            new OpenCodePromptRequest { MessageId = "msg_request",
                Prompt = "List the files.",
                Model = "test-provider/test-model",
                Agent = "build"
            });

        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("Hello \n\nworld", result.OutputText);
        Assert.Equal("stop", result.FinishReason);
        Assert.NotNull(result.Tokens);
        Assert.Equal(1234, result.Tokens.Input);
        Assert.Equal(567, result.Tokens.Output);
        Assert.Null(result.ErrorMessage);

        var tool = Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", tool.CallId);
        Assert.Equal("read", tool.Tool);
        Assert.Equal("completed", tool.Status);
        Assert.Equal(
            @"C:\workspace\demo-app\README.md",
            tool.Input.GetProperty("filePath").GetString());

        Assert.Contains(("POST", "/session"), handler.Requests);
        Assert.Contains(("POST", "/session/ses_full/prompt_async"), handler.Requests);
        Assert.Contains(("GET", "/event"), handler.Requests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_WhenSameTextPartUpdates_KeepsLatestPartText()
    {
        const string FirstTextUpdate =
            """{"type":"message.part.updated","properties":{"part":{"id":"prt_text_1","sessionID":"ses_update","messageID":"msg_1","type":"text","text":"Hel"}}}""";
        const string SecondTextUpdate =
            """{"type":"message.part.updated","properties":{"part":{"id":"prt_text_1","sessionID":"ses_update","messageID":"msg_1","type":"text","text":"Hello"}}}""";
        const string Finish =
            """{"type":"message.part.updated","properties":{"part":{"id":"prt_step_1","sessionID":"ses_update","messageID":"msg_1","type":"step-finish","reason":"stop"}}}""";

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_update")));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_update/prompt_async")
            {
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(SseResponse(ServerConnected, BoundAssistant("ses_update"), FirstTextUpdate, SecondTextUpdate, Finish));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_update", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("Hello", result.OutputText);
    }

    [Theory]
    [InlineData("session.error")]
    [InlineData("error")]
    public async Task ExecuteTurnAsync_WhenStreamReportsError_ReturnsFailedTurn(string errorType)
    {
        var errorEvent = $$$"""{"type":"{{{errorType}}}","properties":{"sessionID":"ses_error","message":"provider unavailable"}}""";

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_error")));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_error/prompt_async")
            {
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(SseResponse(ServerConnected, errorEvent));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_error", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Equal("provider unavailable", result.ErrorMessage);
        Assert.Equal(string.Empty, result.OutputText);
    }

    [Fact]
    public async Task CancelTurnAsync_HotTerminalPublishedDuringAbort_IsNotLost()
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var original = new PromptTriggeredStream(ServerConnected);
        using var confirmation = new PromptTriggeredStream(ServerConnected);
        var connections = 0; var abortCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_hot_abort")));
            if (request.Method == HttpMethod.Get && path == "/event")
                return Task.FromResult(StreamSseResponse(Interlocked.Increment(ref connections) == 1 ? original : confirmation));
            if (request.Method == HttpMethod.Post && path.EndsWith("/prompt_async", StringComparison.Ordinal))
            { prompt.TrySetResult(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (request.Method == HttpMethod.Post && path.EndsWith("/abort", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref abortCount);
                // Real instance buses do not replay an idle event to a subscriber connected later.
                if (Volatile.Read(ref connections) >= 2)
                    confirmation.Publish(BoundAssistant("ses_hot_abort", completed: true));
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }
            throw new InvalidOperationException("Unexpected hot-bus fixture request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, cancellationTimeout: TimeSpan.FromMilliseconds(250));
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        var execution = service.ExecuteTurnAsync("ses_hot_abort", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "synthetic task" });
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await service.CancelTurnAsync("ses_hot_abort").WaitAsync(TimeSpan.FromSeconds(10)));
        var result = await execution;
        Assert.Equal(TurnResult.CancelledStatus, result.Status); Assert.False(result.IsDeliveryUncertain);
        Assert.Equal(1, abortCount); Assert.True(connections >= 2);
    }

    [Fact]
    public async Task CancelTurnAsync_AbortsAndConfirmsCancellationFromEventStream()
    {
        var abortReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activeStream = new PromptTriggeredStream(ServerConnected);
        using var confirmationStream = new PromptTriggeredStream(ServerConnected);
        var connections = 0;
        var abortCount = 0;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_cancel")));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_cancel/prompt_async")
            {
                promptReceived.TrySetResult();
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_cancel/abort")
            {
                Interlocked.Increment(ref abortCount);
                abortReceived.TrySetResult();
                confirmationStream.Publish(BoundAssistant("ses_cancel", completed: true));
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(StreamSseResponse(
                    Interlocked.Increment(ref connections) == 1 ? activeStream : confirmationStream));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var executeTask = service.ExecuteTurnAsync("ses_cancel", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "long task" });
        await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = await service.CancelTurnAsync("ses_cancel");

        Assert.True(cancelled);

        var result = await executeTask;

        Assert.Equal(TurnResult.CancelledStatus, result.Status);
        Assert.Equal(1, abortCount);
        Assert.Contains(("POST", "/session/ses_cancel/prompt_async"), handler.Requests);
        Assert.Contains(("POST", "/session/ses_cancel/abort"), handler.Requests);
    }

    [Theory]
    [InlineData("unconfirmed-stream")]
    [InlineData("blocked-handshake")]
    [InlineData("foreign-terminal")]
    [InlineData("no-terminal")]
    public async Task CancelTurnAsync_WithoutMatchingTerminal_RemainsUnconfirmed(string scenario)
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var original = new PromptTriggeredStream(ServerConnected);
        using var confirmation = new PromptTriggeredStream(scenario == "blocked-handshake" ? null :
            scenario == "unconfirmed-stream"
                ? """{"type":"session.idle","properties":{"sessionID":"ses_cancel_guard"}}"""
                : ServerConnected);
        var connections = 0;
        var aborts = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_cancel_guard")));
            if (request.Method == HttpMethod.Get && path == "/event")
                return Task.FromResult(StreamSseResponse(
                    Interlocked.Increment(ref connections) == 1 ? original : confirmation));
            if (request.Method == HttpMethod.Post && path.EndsWith("/prompt_async", StringComparison.Ordinal))
            { prompt.TrySetResult(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (request.Method == HttpMethod.Post && path.EndsWith("/abort", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref aborts);
                if (scenario == "foreign-terminal")
                    confirmation.Publish("""{"type":"session.idle","properties":{"sessionID":"ses_other"}}""");
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }
            throw new InvalidOperationException("Unexpected cancellation guard request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, cancellationTimeout: TimeSpan.FromMilliseconds(250));
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        var execution = service.ExecuteTurnAsync("ses_cancel_guard", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "synthetic task" });
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await service.CancelTurnAsync("ses_cancel_guard").WaitAsync(TimeSpan.FromSeconds(10)));
        var result = await execution;
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.True(result.IsDeliveryUncertain);
        Assert.False(result.WasTimedOut);
        Assert.Equal(scenario is "unconfirmed-stream" or "blocked-handshake" ? 0 : 1, aborts);
    }

    [Fact]
    public async Task ExecuteTurnAsync_WhenCallerCancels_AbortsAndReturnsCancelled()
    {
        var abortReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activeStream = new PromptTriggeredStream(ServerConnected);
        using var confirmationStream = new PromptTriggeredStream(ServerConnected);
        var connections = 0;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_token")));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_token/prompt_async")
            {
                promptReceived.TrySetResult();
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_token/abort")
            {
                abortReceived.TrySetResult();
                confirmationStream.Publish(BoundAssistant("ses_token", completed: true));
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(StreamSseResponse(
                    Interlocked.Increment(ref connections) == 1 ? activeStream : confirmationStream));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        using var cancellation = new CancellationTokenSource();
        var executeTask = service.ExecuteTurnAsync(
            "ses_token",
            new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "long task" },
            cancellation.Token);

        await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var result = await executeTask;

        Assert.Equal(TurnResult.CancelledStatus, result.Status);
        Assert.Contains(("POST", "/session/ses_token/prompt_async"), handler.Requests);
        Assert.Contains(("POST", "/session/ses_token/abort"), handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetSessionAsync_CreatesNewNativeSessionOnlyWithoutForeignWriterLock(bool locked)
    {
        var sessionCreateCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                var count = Interlocked.Increment(ref sessionCreateCount);

                return Task.FromResult(StubHttpMessageHandler.Json(
                    SessionJson(count == 1 ? "ses_old" : "ses_reset")));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);

        var activeLock = ProjectLock.Acquire(
            "lock_1",
            "prj_1",
            @"C:\workspace\demo-app",
            "exec_1",
            "app_1",
            1,
            DateTimeOffset.UtcNow);
        var lockRepository = new RecordingProjectLockRepository { ActiveLock = locked ? activeLock : null };
        var service = CreateService(httpClient, lockRepository);

        var request = new OpenCodeCreateSessionRequest
        {
            Directory = @"C:\workspace\demo-app",
            Model = "test-model"
        };

        var oldSession = await service.CreateAndConfirmSessionAsync(request);

        if (locked)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetSessionAsync(oldSession.Id, request));
            Assert.Equal(1, sessionCreateCount);
            Assert.Equal(0, lockRepository.ReleaseCount);
            Assert.True(activeLock.IsHeld);
            return;
        }

        var resetSession = await service.ResetSessionAsync(oldSession.Id, request);

        Assert.Equal("ses_reset", resetSession.Id);
        Assert.Equal("ses_old", resetSession.ParentId);
        Assert.Equal(new[] { "ses_old" }, service.GetAncestry(resetSession.Id));

        Assert.Equal(0, lockRepository.ReleaseCount);
        Assert.Equal(@"C:\workspace\demo-app", lockRepository.RequestedRootPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteTurnAsync("ses_old", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" }));
    }

    [Fact]
    public async Task ResetSessionAsync_TracksMultiGenerationAncestry()
    {
        var sessionCreateCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                var count = Interlocked.Increment(ref sessionCreateCount);

                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson($"ses_gen{count}")));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var request = new OpenCodeCreateSessionRequest { Model = "test-model" };
        var first = await service.CreateAndConfirmSessionAsync(request);
        var second = await service.ResetSessionAsync(first.Id, request);
        var third = await service.ResetSessionAsync(second.Id, request);

        Assert.Equal("ses_gen1", first.Id);
        Assert.Equal("ses_gen1", second.ParentId);
        Assert.Equal("ses_gen2", third.ParentId);
        Assert.Equal(new[] { "ses_gen1", "ses_gen2" }, service.GetAncestry(third.Id));
        Assert.Empty(service.GetAncestry(first.Id));
    }

    [Fact]
    public async Task ResetSessionAsync_WhenNewSessionCreationFails_KeepsOldSessionUsable()
    {
        var sessionCreateCount = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                var count = Interlocked.Increment(ref sessionCreateCount);

                return Task.FromResult(count == 1
                    ? StubHttpMessageHandler.Json(SessionJson("ses_keep"))
                    : StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError));
            }

            if (request.Method == HttpMethod.Post && path == "/session/ses_keep/prompt_async")
            {
                return Task.FromResult(StubHttpMessageHandler.Json("true"));
            }

            if (request.Method == HttpMethod.Get && path == "/event")
            {
                return Task.FromResult(SseResponse(
                    ServerConnected,
                    BoundAssistant("ses_keep"),
                    """{"type":"message.part.updated","properties":{"part":{"id":"prt_1","sessionID":"ses_keep","messageID":"msg_1","type":"step-finish","reason":"stop"}}}"""));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var request = new OpenCodeCreateSessionRequest { Model = "test-model" };
        var oldSession = await service.CreateAndConfirmSessionAsync(request);

        await Assert.ThrowsAsync<OpenCodeClientException>(
            () => service.ResetSessionAsync(oldSession.Id, request));

        var result = await service.ExecuteTurnAsync(oldSession.Id, new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task ContinueSessionAsync_ReusesNativeIdAndRejectsChangedBinding()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post && path == OpenCodeApiPaths.Sessions)
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_continue")));
            }

            if (request.Method == HttpMethod.Get && path == "/session/ses_continue")
            {
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_continue")));
            }

            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var session = await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest
        {
            Model = "test-model",
            Agent = "build"
        });

        var binding = new SessionBinding(
            BackendType.OpenCode,
            "profile_1",
            "account_1",
            "test-model",
            reasoningEffort: null,
            speedMode: null,
            executionMode: null);

        var continued = await service.ContinueSessionAsync(session.Id, binding);

        Assert.Equal(session.Id, continued.Id);
        Assert.Contains(("GET", "/session/ses_continue"), handler.Requests);

        var changedBinding = new SessionBinding(
            BackendType.OpenCode,
            "profile_1",
            "account_1",
            "other-model",
            reasoningEffort: null,
            speedMode: null,
            executionMode: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ContinueSessionAsync(session.Id, changedBinding));
    }

    [Fact]
    public async Task ExecuteTurnAsync_WhenPromptModelDiffersFromConfirmedBinding_Throws()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == OpenCodeApiPaths.Sessions
                ? Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_binding")))
                : throw new InvalidOperationException(
                    $"Unexpected request {request.Method} {request.RequestUri!.AbsolutePath}."));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest
        {
            Model = "test-provider/test-model",
            Agent = "build"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteTurnAsync(
                "ses_binding",
                new OpenCodePromptRequest { MessageId = "msg_request",
                    Prompt = "hi",
                    Model = "other-provider/other-model"
                }));
    }

    [Fact]
    public async Task Client_SessionEndpoints_TargetDocumentedPaths()
    {
        const string SessionOne = """
        {
          "id": "ses_1",
          "projectID": "prj_1",
          "directory": "C:\\workspace\\demo-app",
          "title": "Session one",
          "version": "1.18.31",
          "time": { "created": 1789948800000, "updated": 1789948812000 }
        }
        """;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;

            return (request.Method.Method, path) switch
            {
                ("POST", "/session") => Task.FromResult(StubHttpMessageHandler.Json(SessionOne)),
                ("GET", "/session/ses_1") => Task.FromResult(StubHttpMessageHandler.Json(SessionOne)),
                ("GET", "/session") => Task.FromResult(StubHttpMessageHandler.Json($"[{SessionOne}]")),
                ("POST", "/session/ses_1/fork") => Task.FromResult(StubHttpMessageHandler.Json(SessionOne)),
                ("POST", "/session/ses_1/abort") => Task.FromResult(StubHttpMessageHandler.Json("true")),
                ("POST", "/session/ses_1/prompt_async") => Task.FromResult(StubHttpMessageHandler.Json("true")),
                _ => throw new InvalidOperationException($"Unexpected request {request.Method} {path}.")
            };
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var created = await client.CreateSessionAsync(new OpenCodeCreateSessionRequest { Title = "Session one" });
        var fetched = await client.GetSessionAsync("ses_1");
        var listed = await client.ListSessionsAsync();
        var forked = await client.ForkSessionAsync("ses_1");
        var aborted = await client.AbortSessionAsync("ses_1");
        var prompted = await client.SendPromptAsync(
            "ses_1",
            new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal("ses_1", created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("ses_1", fetched!.Id);
        Assert.Equal("ses_1", Assert.Single(listed).Id);
        Assert.Equal("ses_1", forked.Id);
        Assert.True(aborted);
        Assert.True(prompted);

        Assert.Equal(
            new[]
            {
                ("POST", "/session"),
                ("GET", "/session/ses_1"),
                ("GET", "/session"),
                ("POST", "/session/ses_1/fork"),
                ("POST", "/session/ses_1/abort"),
                ("POST", "/session/ses_1/prompt_async")
            },
            handler.Requests);
    }

    [Fact]
    public async Task Client_GetSessionAsync_WhenSessionIsMissing_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        Assert.Null(await client.GetSessionAsync("ses_missing"));
    }

    [Fact]
    public async Task Client_CreateSessionAsync_WhenResponseHasNoNativeId_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("""{"title":"missing id"}""")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        await Assert.ThrowsAsync<OpenCodeClientException>(
            () => client.CreateSessionAsync(new OpenCodeCreateSessionRequest()));
    }

    [Theory]
    [InlineData("once", false)]
    [InlineData("always", true)]
    [InlineData("reject", false)]
    public async Task ReplyPermissionAsync_ForwardsStandardResponses(string response, bool remember)
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);

            return StubHttpMessageHandler.Json("true");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var replied = await client.ReplyPermissionAsync(
            "per_1",
            new OpenCodePermissionReply { Response = response, Remember = remember });

        Assert.True(replied);

        var requestEntry = Assert.Single(handler.Requests);
        Assert.Equal("POST", requestEntry.Method);
        Assert.Equal("/permission/per_1/reply", requestEntry.Path);

        using var document = JsonDocument.Parse(body!);
        Assert.Equal(response, document.RootElement.GetProperty("reply").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task ReplyPermissionAsync_WhenResponseLiteralIsBlank_Throws()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("true")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ReplyPermissionAsync(
                "per_1",
                new OpenCodePermissionReply { Response = "  " }));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ConfirmsSubscriptionBeforePrompt_ReceivesImmediateCompletion()
    {
        using var stream = new PromptTriggeredStream(ServerConnected);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_full")));
            if (request.Method == HttpMethod.Get && path == "/event")
            {
                var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            if (request.Method == HttpMethod.Post && path == "/session/ses_full/prompt_async")
            {
                stream.Publish(BoundAssistant("ses_full"), TextPartHello, StepFinish, SessionIdleFull);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_full", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("Hello ", result.OutputText);
        Assert.Equal(new[] { ("POST", "/session"), ("GET", "/event"), ("POST", "/session/ses_full/prompt_async") }, handler.Requests);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(",\"sessionID\":\"ses_full\"", true)]
    [InlineData(",\"sessionID\":\"ses_foreign\"", false)]
    [InlineData(",\"sessionID\":\"\"", false)]
    [InlineData(",\"sessionID\":null", false)]
    [InlineData(",\"sessionID\":123", false)]
    public async Task ExecuteTurnAsync_ConcreteEnvelopeIdentity_PreservesPartWithoutNestedId(
        string nestedIdentity, bool accepted)
    {
        var partEvent = "{\"type\":\"message.part.updated\",\"properties\":{\"sessionID\":\"ses_full\",\"part\":{\"id\":\"prt_top\",\"messageID\":\"msg_top\",\"type\":\"text\",\"text\":\"top-level text\""
            + nestedIdentity + "}}}";
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_full")));
            if (request.Method == HttpMethod.Get && path == "/event")
                return Task.FromResult(SseResponse(ServerConnected, BoundAssistant("ses_full", "msg_top"), partEvent, BoundAssistant("ses_full", "msg_top", completed: true), SessionIdleFull));
            if (request.Method == HttpMethod.Post && path == "/session/ses_full/prompt_async")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            throw new InvalidOperationException($"Unexpected request {request.Method} {path}.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_full", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal(accepted ? "top-level text" : string.Empty, result.OutputText);
    }

    [Fact]
    public async Task ExecuteTurnAsync_UnconfirmedStreamTimesOutWithoutSendingPrompt()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_unconfirmed")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(BlockingSseResponse());
            throw new InvalidOperationException("No prompt may be sent before stream confirmation.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, turnTimeout: TimeSpan.FromSeconds(30),
            connectionTimeout: TimeSpan.FromMilliseconds(100));
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_unconfirmed", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Contains("event stream was not confirmed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, item => item.Path.EndsWith("/prompt_async", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LifecycleOptions_RejectNonPositiveConnectionBudget(int milliseconds)
    {
        var options = new OpenCodeSessionLifecycleOptions { ConnectionTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"APIError\",\"data\":{\"statusCode\":502}}")]
    public async Task ExecuteTurnAsync_NativeErrorWithoutMessage_NeverCompletes(string error)
    {
        var eventJson = "{\"type\":\"session.error\",\"properties\":{\"sessionID\":\"ses_error\",\"error\":" + error + "}}";
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_error")));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(SseResponse(ServerConnected, eventJson));
            throw new InvalidOperationException("Unexpected request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_error", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Equal("The native session reported an error without a supported message.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteTurnAsync_TerminalBeforeConnected_RefusesPrompt()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_early")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(SseResponse("""{"type":"session.idle","properties":{"sessionID":"ses_early"}}"""));
            throw new InvalidOperationException("No prompt may be sent on an unconfirmed bus.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_early", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Contains("did not begin with server.connected", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, item => item.Path.EndsWith("/prompt_async", StringComparison.Ordinal));
    }

    private sealed class PromptTriggeredStream : BlockingStream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private ReadOnlyMemory<byte> _pending;

        public PromptTriggeredStream(string? connected)
        {
            if (connected is not null) Publish(connected);
        }

        public void Publish(params string[] events)
        {
            if (!_chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(string.Concat(events.Select(item => $"data: {item}\n\n")))))
                throw new InvalidOperationException("SSE fixture was disposed.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
                _pending = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTurnAsync_CancelBeforePrompt_IsLocalAndConfirmed(bool useCancelCommand)
    {
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_connect_cancel")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
            {
                subscribed.TrySetResult();
                return Task.FromResult(BlockingSseResponse());
            }
            throw new InvalidOperationException("No native prompt or abort may run before prompt attempt.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        using var caller = new CancellationTokenSource();
        var execution = service.ExecuteTurnAsync("ses_connect_cancel", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" }, caller.Token);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (useCancelCommand)
            Assert.True(await service.CancelTurnAsync("ses_connect_cancel").WaitAsync(TimeSpan.FromSeconds(10)));
        else
            caller.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TurnResult.CancelledStatus, result.Status);
        Assert.DoesNotContain(handler.Requests, item => item.Path.EndsWith("/prompt_async", StringComparison.Ordinal) || item.Path.EndsWith("/abort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTurnAsync_DeadlineUsesInjectedClock(bool afterPrompt)
    {
        var clock = new ManualBudgetClock();
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var active = new PromptTriggeredStream(ServerConnected);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_clock")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(afterPrompt ? StreamSseResponse(active) : BlockingSseResponse());
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session/ses_clock/prompt_async")
            {
                prompt.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException("Unexpected request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, connectionTimeout: TimeSpan.FromHours(1),
            turnTimeout: TimeSpan.FromHours(2), timeProvider: clock);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        var execution = service.ExecuteTurnAsync("ses_clock", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });
        if (afterPrompt)
            await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(afterPrompt ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1), clock.Timer!.DueTime);
        clock.Timer.Fire();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Contains(afterPrompt ? "turn did not complete" : "stream was not confirmed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(result.WasTimedOut);
        Assert.Equal(afterPrompt, result.IsDeliveryUncertain);
    }

    [Fact]
    public async Task CancelTurnAsync_BlockedNativeAbort_IsBoundedAndUnconfirmed()
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualBudgetClock();
        using var active = new PromptTriggeredStream(ServerConnected);
        using var confirmation = new PromptTriggeredStream(ServerConnected);
        var connections = 0;
        var handler = new StubHttpMessageHandler((request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_abort_budget")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(StreamSseResponse(
                    Interlocked.Increment(ref connections) == 1 ? active : confirmation));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal))
            {
                prompt.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/abort", StringComparison.Ordinal))
            {
                abort.TrySetResult();
                return BlockingTransportReplyAsync(token);
            }
            throw new InvalidOperationException("Unexpected request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, cancellationTimeout: TimeSpan.FromHours(1), timeProvider: clock);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        var execution = service.ExecuteTurnAsync("ses_abort_budget", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancellation = service.CancelTurnAsync("ses_abort_budget");
        await abort.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Timer!.Fire();
        Assert.False(await cancellation.WaitAsync(TimeSpan.FromSeconds(10)));
        var result = await execution;

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Contains("cancellation is unconfirmed", result.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class ManualBudgetClock : TimeProvider
    {
        public bool FireOnChange { get; init; }
        public ManualBudgetTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualBudgetTimer(callback, state, dueTime) { FireOnChange = FireOnChange };
            return Timer;
        }
    }

    [Fact]
    public async Task CancelTurnAsync_UnexpectedTransportFailure_CompletesStopWaiter()
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var active = new PromptTriggeredStream(ServerConnected);
        var handler = new StubHttpMessageHandler((request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_exception")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(StreamSseResponse(active));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal))
            {
                prompt.TrySetResult();
                return BlockingTransportReplyAsync(token, unexpectedFailure: true);
            }
            throw new InvalidOperationException("Unexpected request.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });
        var execution = service.ExecuteTurnAsync("ses_exception", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await service.CancelTurnAsync("ses_exception").WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
    }

    private static async Task<HttpResponseMessage> BlockingTransportReplyAsync(
        CancellationToken cancellationToken, bool unexpectedFailure = false)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (unexpectedFailure)
        {
            throw new InvalidOperationException("Synthetic unexpected transport failure.");
        }
        throw new InvalidOperationException("Blocking HTTP fixture cannot produce a response.");
    }

    private sealed class ManualBudgetTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public bool FireOnChange { get; init; }
        private bool _disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public bool Change(TimeSpan due, TimeSpan period) { if (_disposed) return false; DueTime = due; if (FireOnChange) Fire(); return true; }
        public void Fire() { if (_disposed) throw new ObjectDisposedException(nameof(ManualBudgetTimer)); callback(state); }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ExecuteTurnAsync_DeadlineAtConfirmedBoundary_DoesNotClaimPromptAttempt()
    {
        var clock = new ManualBudgetClock { FireOnChange = true };
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(SessionJson("ses_boundary")));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/event")
                return Task.FromResult(SseResponse(ServerConnected));
            throw new InvalidOperationException("Expired boundary cannot send a prompt.");
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient, timeProvider: clock);
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "test-model" });

        var result = await service.ExecuteTurnAsync("ses_boundary", new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "hi" });

        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Equal("The connection budget expired before the prompt attempt; the prompt was not sent.", result.ErrorMessage);
        Assert.DoesNotContain(handler.Requests, item => item.Path.EndsWith("/prompt_async", StringComparison.Ordinal));
    }

    private static OpenCodeSessionLifecycleService CreateService(
        HttpClient httpClient,
        IProjectLockRepository? projectLockRepository = null,
        TimeSpan? turnTimeout = null,
        TimeSpan? connectionTimeout = null,
        TimeSpan? cancellationTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var streamOptions = new OpenCodeStreamOptions
        {
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(10)
        };

        var eventStreamService = new OpenCodeEventStreamService(httpClient, BaseUrl, options: streamOptions);
        var client = new OpenCodeClient(httpClient, BaseUrl, eventStreamService: eventStreamService);
        var options = new OpenCodeSessionLifecycleOptions
        {
            TurnTimeout = turnTimeout ?? TimeSpan.FromSeconds(10),
            ConnectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(2),
            CancellationTimeout = cancellationTimeout ?? TimeSpan.FromSeconds(5)
        };

        return new OpenCodeSessionLifecycleService(client, projectLockRepository, options, timeProvider);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string SessionJson(string id)
    {
        return $$"""
        {
          "id": "{{id}}",
          "slug": "slug-{{id}}",
          "projectID": "prj_1",
          "directory": "C:\\workspace\\demo-app",
          "title": "Test session",
          "version": "1.18.31",
          "model": { "id": "test-model", "providerID": "test-provider" },
          "cost": 0,
          "tokens": { "input": 1, "output": 2, "reasoning": 3, "cache": { "read": 4, "write": 5 } },
          "time": { "created": 1789948800000, "updated": 1789948812000 }
        }
        """;
    }

    private static HttpResponseMessage SseResponse(params string[] payloads)
    {
        var builder = new StringBuilder();

        foreach (var payload in payloads)
        {
            builder.Append("data: ").Append(payload).Append('\n').Append('\n');
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    // Synthetic native message metadata, explicitly bound to each test prompt.
    private static string BoundAssistant(string session, string id = "msg_1", bool completed = false, string? modelId = null) =>
        JsonSerializer.Serialize(new { type = "message.updated", properties = new { info = new
        {
            id, sessionID = session, role = "assistant", parentID = "msg_request",
            finish = completed ? "stop" : null, time = new { completed = completed ? (long?)1 : null },
            model = modelId is null ? null : new { providerID = "opencode", modelID = modelId }
        } } });

    private static HttpResponseMessage BlockingSseResponse()
    {
        var content = new StreamContent(new BlockingStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage StreamSseResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingProjectLockRepository : IProjectLockRepository
    {
        public ProjectLock? ActiveLock { get; set; }

        public string? RequestedRootPath { get; private set; }

        public string? ReleasedLockId { get; private set; }

        public string? ReleaseReason { get; private set; }

        public int ReleaseCount { get; private set; }

        public Task<ProjectLock?> GetActiveByRootPathAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            RequestedRootPath = rootPath;

            return Task.FromResult(ActiveLock);
        }

        public Task<bool> TryAcquireAsync(
            ProjectLock projectLock,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(
            string lockId,
            DateTimeOffset releasedAt,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ReleasedLockId = lockId;
            ReleaseReason = reason;
            ReleaseCount++;

            return Task.FromResult(true);
        }
    }

    private class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

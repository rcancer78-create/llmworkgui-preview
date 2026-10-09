using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

// The wall-clock responsiveness assertion must not compete with unrelated process/CPU fixtures.
// Concurrency inside these lifecycle scenarios remains enabled; the two-second bound is unchanged.
[CollectionDefinition("OpenCode permission responsiveness", DisableParallelization = true)]
public sealed class OpenCodePermissionResponsivenessCollection;

[Collection("OpenCode permission responsiveness")]
public sealed class OpenCodePermissionLifecycleTests
{
    [Fact]
    public async Task HotStream_DeliversPublishedBytesWithoutPumpingCallerContext()
    {
        using var stream = new HotStream();
        var context = new PausedSynchronizationContext();
        var buffer = new byte[128];
        var originalContext = SynchronizationContext.Current;
        Task<int> read;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            read = stream.ReadAsync(buffer.AsMemory()).AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        try
        {
            stream.Publish("fixture payload");
            // Either delivery finishes or a captured continuation reaches the paused context.
            // The deadline only bounds a broken fixture; it does not measure scheduler speed.
            await Task.WhenAny(read, context.ContinuationPosted).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(context.ContinuationPosted.IsCompleted,
                "The fixture stream must deliver bytes without waiting for its caller's synchronization context.");
            var length = await read.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("data: fixture payload\n\n", Encoding.UTF8.GetString(buffer, 0, length));
        }
        finally
        {
            context.Resume();
        }
    }

    [Fact]
    public async Task LargePermission_RemainsResponsiveAndRedactsBeforeTruncation()
    {
        using var fixture = await Fixture.Start();
        const string syntheticSecret = "synthetic-large-permission-fixture";
        var request = Ask(pattern: "api_key=" + syntheticSecret + "; " + new string('x', 512_000));
        var timer = Stopwatch.StartNew();
        fixture.Publish(request);
        var pending = await fixture.Pending();
        timer.Stop();
        Assert.True(pending.IsDisplayTruncated);
        Assert.False(pending.CanAllowOnce);
        Assert.True(pending.CanDeny);
        Assert.DoesNotContain(syntheticSecret, pending.OriginalRequestDisplay);
        Assert.Contains("[REDACTED]", pending.OriginalRequestDisplay);
        await fixture.RejectAndFinish(pending);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Permission processing took {timer.Elapsed}.");
    }

    [Fact]
    public async Task CapturedNativeAskedAndReplied_AreAcceptedVerbatimForExactSession()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LLMWorkGUI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "docs/protocols/opencode/native-permission-events-20261003.json")));
        var events = document.RootElement.GetProperty("capturedEvents").EnumerateArray().ToArray();
        var session = events[0].GetProperty("properties").GetProperty("sessionID").GetString()!;
        using var fixture = await Fixture.Start(session);
        fixture.Publish(events[0].GetRawText()); var pending = await fixture.Pending();
        Assert.True(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        Assert.Equal(NormalizedApprovalKind.WriteFile, pending.Kind);
        fixture.Publish(events[1].GetRawText());
        await fixture.Until(() => fixture.Service.GetPendingPermissions(session).Count == 0);
        Assert.Empty(fixture.Replies); fixture.FinishNative();
        Assert.Equal(TurnResult.CompletedStatus, (await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }
    [Theory]
    [InlineData("once")]
    [InlineData("reject")]
    public async Task Reply_IsExplicitSingleRequestAndDirectoryScoped(string response)
    {
        using var fixture = await Fixture.Start();
        fixture.Publish(Ask());
        var pending = await fixture.Pending();
        Assert.Empty(fixture.Replies);
        Assert.Equal(NormalizedApprovalKind.WriteFile, pending.Kind);
        Assert.True(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        Assert.False(pending.CanAllowForExecution);
        Assert.True(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, response));
        var reply = Assert.Single(fixture.Replies);
        Assert.Equal("/permission/per_owned/reply", reply.Path);
        Assert.Equal(Fixture.Directory, Uri.UnescapeDataString(reply.Query["?directory=".Length..]));
        using var body = JsonDocument.Parse(reply.Body);
        Assert.Equal(response, body.RootElement.GetProperty("reply").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, response));
        fixture.FinishNative();
        Assert.Equal(TurnResult.CompletedStatus, (await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Empty(fixture.Service.GetPendingPermissions("ses_owned"));
    }

    [Fact]
    public async Task ForeignSessionAndReceipt_AreNeverRepliedTo()
    {
        using var fixture = await Fixture.Start();
        fixture.Publish(Ask(session: "ses_foreign"), Ask());
        var pending = await fixture.Pending();
        Assert.Equal("ses_owned", pending.SessionId);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_foreign", pending.ReceiptId, "once"));
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", "stale-local-receipt", "once"));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "always"));
        Assert.Empty(fixture.Replies);
        await fixture.RejectAndFinish(pending);
    }

    [Fact]
    public async Task DuplicateAndChangedRequest_CannotSilentlyReuseApproval()
    {
        using var fixture = await Fixture.Start();
        fixture.Publish(Ask()); var first = await fixture.Pending();
        fixture.Publish(Ask(), Ask(id: "per_barrier"));
        await fixture.Until(() => fixture.Service.GetPendingPermissions("ses_owned").Count == 2);
        Assert.Equal(first.ReceiptId, fixture.Service.GetPendingPermissions("ses_owned")[0].ReceiptId);
        fixture.Publish(Ask(pattern: "other-file.txt"));
        await fixture.Until(() => fixture.Service.GetPendingPermissions("ses_owned")[0].HasConflictingRequest);
        var changed = fixture.Service.GetPendingPermissions("ses_owned")[0];
        Assert.NotEqual(first.ReceiptId, changed.ReceiptId);
        Assert.False(changed.CanAllowOnce); Assert.True(changed.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", first.ReceiptId, "once"));
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", changed.ReceiptId, "once"));
        Assert.Empty(fixture.Replies);
        foreach (var pending in fixture.Service.GetPendingPermissions("ses_owned"))
            Assert.True(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "reject"));
        fixture.Publish(Ask()); // A resolved native ID stays resolved for this turn.
        fixture.FinishNative();
        Assert.Equal(TurnResult.CompletedStatus, (await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(2, fixture.Replies.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedDelivery_DisablesAnySecondReply(bool throwTransport)
    {
        using var fixture = await Fixture.Start(); fixture.ThrowReply = throwTransport; fixture.AcknowledgeReply = false;
        fixture.Publish(Ask()); var pending = await fixture.Pending();
        if (throwTransport)
            await Assert.ThrowsAsync<OpenCodeClientException>(() => fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        else Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        var attempted = Assert.Single(fixture.Service.GetPendingPermissions("ses_owned"));
        Assert.True(attempted.ReplyAttempted); Assert.False(attempted.CanAllowOnce); Assert.False(attempted.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "reject"));
        Assert.Single(fixture.Replies);
        fixture.FinishNative();
        var result = await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.True(result.IsDeliveryUncertain);
    }

    [Theory]
    [InlineData("permission.v2.asked")]
    [InlineData("permission.updated")]
    public async Task UnsupportedWireFormat_IsVisibleButHasNoReplyButtons(string type)
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask(type: type));
        var pending = await fixture.Pending();
        Assert.Equal(NormalizedApprovalKind.UnknownHighRisk, pending.Kind);
        Assert.False(pending.CanAllowOnce); Assert.False(pending.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "reject"));
        Assert.Empty(fixture.Replies);
        await fixture.CancelAndFinish();
    }

    [Fact]
    public async Task MalformedPatterns_AreVisibleAndCannotBeGranted()
    {
        using var fixture = await Fixture.Start();
        fixture.Publish(Ask().Replace("[\"probe.txt\"]", "[42]", StringComparison.Ordinal));
        var pending = await fixture.Pending();
        Assert.False(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        await fixture.RejectAndFinish(pending);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("metadata")]
    [InlineData("patterns")]
    public async Task IncompleteDetails_CanBeRejectedWithoutGranting(string missing)
    {
        using var fixture = await Fixture.Start();
        var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Ask())!;
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(envelope["properties"])!;
        properties.Remove(missing);
        fixture.Publish(JsonSerializer.Serialize(new { type = "permission.asked", properties }));
        var pending = await fixture.Pending(); Assert.False(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        await fixture.RejectAndFinish(pending);
    }

    [Fact]
    public async Task BlockedReply_HasBoundedBudgetAndNoAutomaticRetry()
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask()); var pending = await fixture.Pending();
        fixture.BlockReply = true; fixture.Options.ConnectionTimeout = TimeSpan.FromMilliseconds(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "reject"));
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "reject"));
        Assert.Single(fixture.Replies); Assert.True(Assert.Single(fixture.Service.GetPendingPermissions("ses_owned")).ReplyAttempted);
        await fixture.CancelAndFinish();
    }

    [Fact]
    public async Task UnknownNativePermission_IsHighRiskWithExplicitSingleRequestChoice()
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask(kind: "custom_tool"));
        var pending = await fixture.Pending(); Assert.Equal(NormalizedApprovalKind.UnknownHighRisk, pending.Kind);
        Assert.True(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        await fixture.RejectAndFinish(pending);
    }

    [Fact]
    public async Task OriginalRequest_IsRedactedBeforeDisplay_AndOversizeCannotBeGranted()
    {
        using var fixture = await Fixture.Start();
        // Labelled synthetic credential syntax, never a provider credential.
        const string fixtureValue = "synthetic-permission-fixture-value";
        fixture.Publish(Ask(metadata: new { api_key = fixtureValue }));
        var first = await fixture.Pending(); Assert.DoesNotContain(fixtureValue, first.OriginalRequestDisplay);
        Assert.Contains("[REDACTED]", first.OriginalRequestDisplay);
        await fixture.Service.ReplyPermissionAsync("ses_owned", first.ReceiptId, "reject");
        fixture.Publish(Ask(id: "per_long", pattern: new string('x', 9000)));
        var pending = await fixture.Pending(); Assert.True(pending.IsDisplayTruncated);
        Assert.InRange(pending.OriginalRequestDisplay.Length, 1, 4097);
        Assert.False(pending.CanAllowOnce); Assert.True(pending.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        await fixture.RejectAndFinish(pending);
    }

    [Fact]
    public async Task NativeReplyEvent_ClearsOnlyMatchingOwnedRequest()
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask()); var pending = await fixture.Pending();
        fixture.Publish(ReplyEvent("ses_foreign", "per_owned"), Ask(id: "per_barrier"));
        await fixture.Until(() => fixture.Service.GetPendingPermissions("ses_owned").Count == 2);
        Assert.Contains(fixture.Service.GetPendingPermissions("ses_owned"), p => p.ReceiptId == pending.ReceiptId);
        fixture.Publish(ReplyEvent("ses_owned", "per_owned"));
        await fixture.Until(() => fixture.Service.GetPendingPermissions("ses_owned").Count == 1);
        await fixture.RejectAndFinish(Assert.Single(fixture.Service.GetPendingPermissions("ses_owned")));
    }

    [Fact]
    public async Task PendingAtTerminal_RetainsUncertainOutcome()
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask()); await fixture.Pending(); fixture.FinishNative();
        var result = await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.True(result.IsDeliveryUncertain);
        Assert.Empty(fixture.Replies);
    }

    [Fact]
    public async Task BoundedHistoryOverflow_DoesNotReportSuccessfulCompletion()
    {
        using var fixture = await Fixture.Start();
        fixture.Publish(Enumerable.Range(0, 65).Select(i => Ask(id: "per_" + i)).ToArray());
        var result = await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.True(result.IsDeliveryUncertain);
        Assert.Contains("BufferOverflow", result.ErrorMessage); Assert.Empty(fixture.Replies);
    }

    [Fact]
    public async Task Cancelling_DisablesRepliesBeforeNativeTerminal()
    {
        using var fixture = await Fixture.Start(); fixture.Publish(Ask()); var pending = await fixture.Pending();
        fixture.PublishOnAbort = false;
        var cancellation = fixture.Service.CancelTurnAsync("ses_owned");
        await fixture.Until(() => fixture.Aborts == 1);
        var cancelling = Assert.Single(fixture.Service.GetPendingPermissions("ses_owned"));
        Assert.False(cancelling.CanAllowOnce); Assert.False(cancelling.CanDeny);
        Assert.False(await fixture.Service.ReplyPermissionAsync("ses_owned", pending.ReceiptId, "once"));
        fixture.FinishNative(); Assert.False(await cancellation.WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await fixture.Execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.True(result.IsDeliveryUncertain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteTurnAsync("ses_owned",
            new() { MessageId = "msg_next", Prompt = "Synthetic retry" }));
    }

    private static string ReplyEvent(string session, string request) => JsonSerializer.Serialize(new
        { type = "permission.replied", properties = new { sessionID = session, requestID = request, reply = "reject" } });
    private static string Ask(string id = "per_owned", string session = "ses_owned", string kind = "edit",
        string pattern = "probe.txt", string type = "permission.asked", object? metadata = null) => JsonSerializer.Serialize(new
        { type, properties = new { id, sessionID = session, permission = kind, patterns = new[] { pattern },
            metadata = metadata ?? new { }, always = new[] { "*" }, tool = new { messageID = "msg_fixture", callID = "call_fixture" } } });

    private sealed class Fixture : IDisposable
    {
        public const string Directory = @"D:\owned permission fixture\a+b & c";
        private readonly List<HotStream> _streams = new();
        private readonly object _streamGate = new();
        private readonly TaskCompletionSource _promptDispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HttpClient _http;
        private readonly OpenCodeEventStreamService _events;
        public OpenCodeSessionLifecycleService Service { get; }
        public Task<TurnResult> Execution { get; private set; } = null!;
        public List<(string Path, string Query, string Body)> Replies { get; } = new();
        public bool ThrowReply { get; set; }
        public bool BlockReply { get; set; }
        public OpenCodeSessionLifecycleOptions Options { get; } = new()
        { ConnectionTimeout = TimeSpan.FromSeconds(20), TurnTimeout = TimeSpan.FromSeconds(45), CancellationTimeout = TimeSpan.FromSeconds(5) };
        public bool AcknowledgeReply { get; set; } = true;
        public bool PublishOnAbort { get; set; } = true;
        public int Aborts { get; private set; }
        public string SessionId { get; }
        private Fixture(string sessionId)
        {
            SessionId = sessionId;
            _http = new HttpClient(new StubHttpMessageHandler(async (request, token) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/session") return StubHttpMessageHandler.Json(JsonSerializer.Serialize(new { id = SessionId, directory = Directory }));
                if (uri.AbsolutePath == "/event")
                {
                    var stream = new HotStream();
                    stream.Publish("{\"type\":\"server.connected\",\"properties\":{}}");
                    lock (_streamGate) _streams.Add(stream);
                    var content = new StreamContent(stream); content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }
                if (uri.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal))
                { _promptDispatched.TrySetResult(); return StubHttpMessageHandler.Json("true"); }
                if (uri.AbsolutePath.EndsWith("/abort", StringComparison.Ordinal))
                { Aborts++; if (PublishOnAbort) FinishNative(); return StubHttpMessageHandler.Json("true"); }
                if (uri.AbsolutePath.StartsWith("/permission/", StringComparison.Ordinal))
                {
                    Replies.Add((uri.AbsolutePath, uri.Query, await request.Content!.ReadAsStringAsync(token)));
                    if (BlockReply) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    if (ThrowReply) throw new HttpRequestException("Synthetic unconfirmed delivery.");
                    return StubHttpMessageHandler.Json(AcknowledgeReply ? "true" : "false");
                }
                throw new InvalidOperationException("Unexpected synthetic request: " + uri.AbsolutePath);
            })) { Timeout = Timeout.InfiniteTimeSpan };
            var url = new Uri("http://127.0.0.1:54321");
            _events = new OpenCodeEventStreamService(_http, url);
            Service = new OpenCodeSessionLifecycleService(new OpenCodeClient(_http, url,
                eventStreamService: _events), options: Options);
        }
        public static async Task<Fixture> Start(string sessionId = "ses_owned")
        {
            var result = new Fixture(sessionId);
            await result.Service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Directory = Directory });
            result.Execution = result.Service.ExecuteTurnAsync(sessionId, new OpenCodePromptRequest { MessageId = "msg_request", Prompt = "synthetic permission lifecycle" });
            await result._promptDispatched.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return result;
        }
        public void Publish(params string[] events) { lock (_streamGate) foreach (var stream in _streams) stream.Publish(events); }
        public void FinishNative() => Publish(JsonSerializer.Serialize(new { type = "message.updated", properties = new { info = new
            { id = "msg_fixture", parentID = "msg_request", role = "assistant", sessionID = SessionId, finish = "stop", time = new { completed = 1 } } } }));
        public async Task<OpenCodePendingPermission> Pending()
        { await Until(() => Service.GetPendingPermissions(SessionId).Count > 0); return Assert.Single(Service.GetPendingPermissions(SessionId)); }
        public async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                while (!condition())
                {
                    // Surface an actual lifecycle failure immediately instead of reporting a later polling timeout.
                    if (Execution.IsFaulted) await Execution;
                    if (Execution.IsCompleted)
                        throw new Xunit.Sdk.XunitException("Fixture condition was not reached before execution ended. " + Diagnostics());
                    await Task.Delay(10, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException("Fixture condition was not reached within 15 seconds. " + Diagnostics());
            }
        }
        private string Diagnostics()
        {
            int streams;
            lock (_streamGate) streams = _streams.Count;
            var outcome = Execution.IsCompletedSuccessfully
                ? $"{Execution.Result.Status}; error={Execution.Result.ErrorMessage ?? "none"}" : Execution.Status.ToString();
            var recent = string.Join(",", _events.GetRecentEvents(16).Select(item => item.Type));
            return $"Execution={outcome}; streams={streams}; pending={Service.GetPendingPermissions(SessionId).Count}; "
                + $"replies={Replies.Count}; aborts={Aborts}; recentEvents=[{recent}].";
        }
        public async Task RejectAndFinish(OpenCodePendingPermission pending)
        {
            Assert.True(await Service.ReplyPermissionAsync(SessionId, pending.ReceiptId, "reject")); FinishNative();
            Assert.Equal(TurnResult.CompletedStatus, (await Execution.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        public async Task CancelAndFinish()
        {
            // Every caller deliberately retains an unresolved/uncertain permission reply.
            Assert.False(await Service.CancelTurnAsync(SessionId).WaitAsync(TimeSpan.FromSeconds(5)));
            var result = await Execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.True(result.IsDeliveryUncertain);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ExecuteTurnAsync(SessionId,
                new() { MessageId = "msg_next", Prompt = "Synthetic retry" }));
        }
        public void Dispose() { lock (_streamGate) foreach (var stream in _streams) stream.Dispose(); _http.Dispose(); }
    }

    private sealed class PausedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _continuations = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ContinuationPosted => _posted.Task;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _continuations.Enqueue((callback, state));
            _posted.TrySetResult();
        }
        public void Resume()
        {
            while (_continuations.TryDequeue(out var continuation))
                continuation.Callback(continuation.State);
        }
    }

    private sealed class HotStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> _pending;
        public void Publish(params string[] events) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(string.Concat(events.Select(e =>
            string.Join("\n", e.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "data: " + line)) + "\n\n"))));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty) _pending = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var length = Math.Min(buffer.Length, _pending.Length); _pending[..length].CopyTo(buffer); _pending = _pending[length..]; return length;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _chunks.Writer.TryComplete(); base.Dispose(disposing); }
    }
}

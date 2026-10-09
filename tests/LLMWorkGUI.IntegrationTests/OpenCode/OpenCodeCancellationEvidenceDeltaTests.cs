using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeCancellationEvidenceDeltaTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    public async Task CancellationRacingAnAvailableEventRequiresBoundNativeTerminalEvidence(
        bool boundTerminal, bool pendingPermission, bool resolvedPermission, bool terminalOnReplacement)
    {
        using var directory = new TestDirectory();
        var events = new HeldCancellationEvents(boundTerminal, pendingPermission, resolvedPermission, terminalOnReplacement);
        var expectedConfirmation = (boundTerminal || terminalOnReplacement) && (!pendingPermission || resolvedPermission);
        var sends = 0;
        var aborts = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/session":
                    return Task.FromResult(StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
                    {
                        id = "ses_owned", slug = "owned", projectID = "owned-project", directory = directory.Root,
                        title = "Owned", version = "1.18.31", time = new { created = 1, updated = 1 }
                    })));
                case "/session/ses_owned/prompt_async":
                    Interlocked.Increment(ref sends);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                case "/session/ses_owned/abort":
                    Interlocked.Increment(ref aborts);
                    return Task.FromResult(StubHttpMessageHandler.Json("true"));
                default:
                    throw new InvalidOperationException("Unexpected owned cancellation request.");
            }
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http,
            new Uri("http://127.0.0.1:4970"), eventStreamService: events), options: new()
        {
            ConnectionTimeout = TimeSpan.FromSeconds(3), TurnTimeout = TimeSpan.FromSeconds(10),
            CancellationTimeout = TimeSpan.FromMilliseconds(150)
        });
        var session = await service.CreateAndConfirmSessionAsync(new() { Directory = directory.Root, Model = "model" });
        var execution = service.ExecuteTurnAsync(session.Id,
            new() { MessageId = "msg_owned", Prompt = "Owned synthetic input", Model = "model" });
        Task<bool>? cancellation = null;
        try
        {
            await events.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, sends);
            cancellation = service.CancelTurnAsync(session.Id);
            Assert.True(events.OriginalToken.IsCancellationRequested);
            Assert.False(execution.IsCompleted);
            events.Release.TrySetResult();
            var confirmed = await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expectedConfirmation, confirmed);
            Assert.Equal(expectedConfirmation ? TurnResult.CancelledStatus : TurnResult.FailedStatus, result.Status);
            Assert.Equal(!expectedConfirmation, result.IsDeliveryUncertain);
            Assert.Equal(boundTerminal ? 0 : 1, aborts);
            Assert.Equal(events.Subscriptions, events.Disposed);
            if (!expectedConfirmation)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTurnAsync(session.Id,
                    new() { MessageId = "msg_retry", Prompt = "Owned retry", Model = "model" }));
                Assert.Equal(1, sends); // Ambiguous ownership is retained; no replacement prompt.
            }
        }
        finally
        {
            events.Release.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancellation is not null) await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class HeldCancellationEvents(bool boundTerminal, bool pendingPermission,
        bool resolvedPermission, bool terminalOnReplacement) : IOpenCodeEventStreamService
    {
        public bool IsOverflowed => false;
        public string? SpoolFilePath => null;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken OriginalToken { get; private set; }
        public int Subscriptions { get; private set; }
        public int Disposed { get; private set; }
        public IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null) => [];

        public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(string? sessionId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var subscription = ++Subscriptions;
            if (subscription == 1) OriginalToken = cancellationToken;
            try
            {
                yield return Event("server.connected", new { });
                if (subscription != 1)
                {
                    if (terminalOnReplacement)
                    {
                        foreach (var permission in PermissionEvents()) yield return permission;
                        yield return BoundTerminal();
                        yield break;
                    }
                    // An abort acknowledgement has no matching terminal evidence. This actual
                    // replacement iterator observes its owned cancellation budget and is joined.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    yield break;
                }
                if (!terminalOnReplacement)
                    foreach (var permission in PermissionEvents()) yield return permission;
                Waiting.TrySetResult();
                await Release.Task;
                // Exercise the replacement-subscription cancellation path without a session-only
                // error, which deliberately leaves the accumulator's unbound-error flag sticky.
                if (terminalOnReplacement) OriginalToken.ThrowIfCancellationRequested();
                // A frame already available to an iterator can win against token observation.
                // The lifecycle must distinguish a bound terminal from a session-only error.
                yield return boundTerminal
                    ? BoundTerminal()
                    : Event("session.error", new { sessionID = "ses_owned", error = new { message = "Owned session-only failure" } });
            }
            finally { Disposed++; }
        }

        private IEnumerable<OpenCodeEventEnvelope> PermissionEvents()
        {
            if (!pendingPermission) yield break;
            yield return Event("permission.asked", new { id="per_owned", sessionID="ses_owned", permission="edit",
                patterns=new[] {"owned.txt"}, metadata=new {}, always=Array.Empty<string>() });
            if (resolvedPermission)
                yield return Event("permission.replied", new { sessionID="ses_owned", requestID="per_owned", reply="reject" });
        }

        private static OpenCodeEventEnvelope BoundTerminal() => Event("message.updated", new
        {
            info = new { id="msg_assistant", sessionID="ses_owned", role="assistant", parentID="msg_owned",
                finish="stop", time=new {created=1,completed=2} }
        });

        private static OpenCodeEventEnvelope Event(string type, object properties) =>
            new(type, JsonSerializer.SerializeToElement(properties), "Owned synthetic event", DateTime.UtcNow);
    }
}

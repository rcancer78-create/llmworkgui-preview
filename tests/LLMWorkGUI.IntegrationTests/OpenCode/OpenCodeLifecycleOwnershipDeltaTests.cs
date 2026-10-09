using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeLifecycleOwnershipDeltaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetChecksTheActualDestinationAgainstAnExistingSqliteWriter(bool differentLockedDestination)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var oldRoot = database.GetWorkspacePath("old");
        var lockedRoot = database.GetWorkspacePath("locked");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(lockedRoot);
        await database.SeedRouteChainAsync(projectRootPath: lockedRoot);
        await database.SeedSessionAsync(workspaceRootPath: lockedRoot);
        await database.SeedExecutionAsync("owned-lock-execution");
        var locks = new SqliteProjectLockRepository(database.Factory);
        Assert.True(await locks.TryAcquireAsync(ProjectLock.Acquire("owned-lock", "project-1", lockedRoot,
            "owned-lock-execution", "foreign-owner", 1, DateTimeOffset.UtcNow)));
        var creations = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var id = "ses_" + Interlocked.Increment(ref creations);
            return Task.FromResult(StubHttpMessageHandler.Json(Session(id, Destination(request))));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, new Uri("http://127.0.0.1:4970")), locks);
        var old = await service.CreateAndConfirmSessionAsync(new() { Directory = oldRoot, Model = "model" });
        var request = new OpenCodeCreateSessionRequest
            { Directory = differentLockedDestination ? lockedRoot : oldRoot, Model = "model" };

        var failure = await Record.ExceptionAsync(() => service.ResetSessionAsync(old.Id, request));

        if (differentLockedDestination)
        {
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(1, creations);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(2, creations);
        }
        Assert.True((await locks.GetActiveByRootPathAsync(lockedRoot))!.IsHeld);
        Assert.Equal("owned-lock-execution", (await locks.GetActiveByRootPathAsync(lockedRoot))!.ExecutionId);
    }

    [Fact]
    public async Task ResetForAnotherExecution_DoesNotReleaseTheHolderOrDispatch()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var root = database.GetWorkspacePath("locked");
        Directory.CreateDirectory(root);
        await database.SeedRouteChainAsync(projectRootPath: root);
        await database.SeedSessionAsync(workspaceRootPath: root, nativeSessionId: "native-a");
        await database.SeedExecutionAsync("execution-a", state: "Running");
        await database.SeedSessionAsync("session-b", workspaceRootPath: root, nativeSessionId: "native-other", state: "Idle");
        await database.SeedExecutionAsync("execution-b", "session-b");
        var locks = new SqliteProjectLockRepository(database.Factory);
        Assert.True(await locks.TryAcquireAsync(ProjectLock.Acquire("lock-a", "project-1", root,
            "execution-a", "owner-a", 1, DateTimeOffset.UtcNow)));
        var stateBefore = await Scalar(database, "SELECT State FROM Executions WHERE Id='execution-a'");
        var endedBefore = await Scalar(database, "SELECT EndedAtUtc FROM Executions WHERE Id='execution-a'");
        var creations = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var id = "ses_" + Interlocked.Increment(ref creations);
            return Task.FromResult(StubHttpMessageHandler.Json(Session(id, root)));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, new Uri("http://127.0.0.1:4970")), locks);
        var other = await service.CreateAndConfirmSessionAsync(new() { Directory = root, Model = "model" });

        var failure = await Record.ExceptionAsync(() => service.ResetSessionAsync(other.Id, new() { Directory = root, Model = "model" }));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, creations);
        var held = await locks.GetActiveByRootPathAsync(root);
        Assert.True(held!.IsHeld);
        Assert.Equal("execution-a", held.ExecutionId);
        Assert.Equal("lock-a", held.Id);
        Assert.Null(await Scalar(database, "SELECT ReleasedAtUtc FROM ProjectLocks WHERE Id='lock-a'"));
        Assert.Equal(stateBefore, await Scalar(database, "SELECT State FROM Executions WHERE Id='execution-a'"));
        Assert.Equal(endedBefore, await Scalar(database, "SELECT EndedAtUtc FROM Executions WHERE Id='execution-a'"));
        Assert.False(await locks.TryAcquireAsync(ProjectLock.Acquire("lock-b", "project-1", root,
            "execution-b", "owner-b", 1, DateTimeOffset.UtcNow)));

        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Accounts SET MaxConcurrentExecutions=3 WHERE Id='account-1'";
            await command.ExecuteNonQueryAsync();
        }
        using var guard = new ApplicationInstanceGuard(Path.Combine(database.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(database.Factory, TimeProvider.System, guard);
        var route = Assert.Single(await journal.ListRoutesAsync());
        var otherEntry = await journal.BeginAsync("project-1", root, "native-b", route, "request-b", "fixture-prompt-hash", 17);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(otherEntry));
        Assert.False(await journal.AuthorizePromptDispatchAsync(otherEntry, otherEntry.NativeSessionId,
            new OpenCodePromptRequest { Prompt = "fixture", Model = route.NativeModelId },
            new Uri("http://127.0.0.1:4970/session/native-b/prompt_async?directory=" + Uri.EscapeDataString(root))));

        held = await locks.GetActiveByRootPathAsync(root);
        Assert.Equal("execution-a", held!.ExecutionId);
        Assert.True(held.IsHeld);
        Assert.Null(await Scalar(database, "SELECT ReleasedAtUtc FROM ProjectLocks WHERE Id='lock-a'"));
        Assert.Equal(stateBefore, await Scalar(database, "SELECT State FROM Executions WHERE Id='execution-a'"));
        Assert.Equal(endedBefore, await Scalar(database, "SELECT EndedAtUtc FROM Executions WHERE Id='execution-a'"));
        Assert.Equal("SessionConfirmed", await Scalar(database, "SELECT State FROM Executions WHERE Id='" + otherEntry.ExecutionId + "'"));
    }

    [Fact]
    public async Task ContinueCannotPublishAnOldHeldGetAfterResetClosesThatSession()
    {
        using var directory = new TestDirectory();
        var entered = Signal();
        var release = Signal();
        var creations = 0;
        var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
                return StubHttpMessageHandler.Json(Session("ses_" + Interlocked.Increment(ref creations), directory.Root));
            var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (id == "ses_1") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return StubHttpMessageHandler.Json(Session(id, directory.Root));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, new Uri("http://127.0.0.1:4970")));
        var request = new OpenCodeCreateSessionRequest { Directory = directory.Root, Model = "model" };
        var old = await service.CreateAndConfirmSessionAsync(request);
        var pending = service.ContinueSessionAsync(old.Id, Binding("account-a"));
        try
        {
            await entered.Task.WaitAsync(Wait);
            var reset = await service.ResetSessionAsync(old.Id, request);
            release.TrySetResult();
            var failure = await Record.ExceptionAsync(() => pending.WaitAsync(Wait));
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(reset.Id, (await service.ContinueSessionAsync(reset.Id, Binding("account-a"))).Id);
        }
        finally
        {
            release.TrySetResult();
            try { await pending.WaitAsync(Wait); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task FailedContinueGetDoesNotPublishAnUnconfirmedBinding()
    {
        using var directory = new TestDirectory();
        var gets = 0;
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get && Interlocked.Increment(ref gets) == 1
                ? StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
                : StubHttpMessageHandler.Json(Session("ses_owned", directory.Root))));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, new Uri("http://127.0.0.1:4970")));
        var session = await service.CreateAndConfirmSessionAsync(new() { Directory = directory.Root, Model = "model" });
        await Assert.ThrowsAsync<OpenCodeClientException>(() => service.ContinueSessionAsync(session.Id, Binding("unconfirmed")));

        var failure = await Record.ExceptionAsync(() => service.ContinueSessionAsync(session.Id, Binding("confirmed")));

        Assert.Null(failure);
        Assert.Equal(2, gets);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ContinueSessionAsync(session.Id, Binding("changed")));
    }

    [Theory]
    [InlineData("error", true)]
    [InlineData("error", false)]
    [InlineData("finish", true)]
    [InlineData("finish", false)]
    [InlineData("blank-error", false)]
    [InlineData("empty-error", false)]
    [InlineData("empty-tool-id", false)]
    [InlineData("text", true)]
    [InlineData("text", false)]
    [InlineData("parts", true)]
    [InlineData("parts", false)]
    [InlineData("messages", true)]
    [InlineData("messages", false)]
    [InlineData("tools", true)]
    [InlineData("tools", false)]
    public async Task AggregateDefaultBoundsRefuseUncertainOverflowAndJoinTheOwnedIterator(string dimension, bool overflow)
    {
        using var directory = new TestDirectory();
        var events = new GeneratedEvents(dimension, overflow);
        var creations = 0;
        var promptSends = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/session")
            {
                Interlocked.Increment(ref creations);
                return Task.FromResult(StubHttpMessageHandler.Json(Session("ses_owned", directory.Root)));
            }
            Assert.Equal("/session/ses_owned/prompt_async", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref promptSends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OpenCodeClient(http, new Uri("http://127.0.0.1:4970"), eventStreamService: events);
        var service = new OpenCodeSessionLifecycleService(client);
        var session = await service.CreateAndConfirmSessionAsync(new() { Directory = directory.Root, Model = "model" });
        var turn = service.ExecuteTurnAsync(session.Id, new() { MessageId = "msg_owned", Prompt = "Synthetic input", Model = "model" });
        try
        {
            await events.Disposing.Task.WaitAsync(Wait);
            Assert.False(turn.IsCompleted, "An owned event iterator must be joined before execution returns.");
            events.ReleaseDisposal.TrySetResult();
            var result = await turn.WaitAsync(Wait);
            Assert.Equal(1, promptSends);
            Assert.Equal(1, creations);
            Assert.True(events.Disposed);
            if (overflow)
            {
                Assert.Equal(TurnResult.FailedStatus, result.Status);
                Assert.Equal("BufferOverflow", result.FinishReason);
                Assert.True(result.IsDeliveryUncertain);
                Assert.DoesNotContain("synthetic-private-part", result.ErrorMessage ?? "");
                Assert.DoesNotContain(directory.Root, result.ErrorMessage ?? "");
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTurnAsync(session.Id,
                    new() { Prompt = "Synthetic retry", Model = "model" }));
                Assert.Equal(1, promptSends);
            }
            else
            {
                Assert.Equal(dimension == "error" ? TurnResult.FailedStatus : TurnResult.CompletedStatus, result.Status);
                Assert.False(result.IsDeliveryUncertain);
                if (dimension == "finish") Assert.Equal("stop", result.FinishReason);
                if (dimension is "blank-error" or "empty-error")
                { Assert.Equal("stop", result.FinishReason); Assert.Null(result.ErrorMessage); Assert.NotEmpty(result.OutputText); }
                if (dimension == "text") Assert.Equal(4 * 1024 * 1024, result.OutputText.Replace("\n\n", "").Length);
                if (dimension is "tools" or "empty-tool-id")
                { Assert.Equal(4096, result.ToolCalls.Count); Assert.All(result.ToolCalls, tool => Assert.False(string.IsNullOrEmpty(tool.CallId))); }
            }
        }
        finally
        {
            events.ReleaseDisposal.TrySetResult();
            await turn.WaitAsync(Wait);
        }
    }

    [Fact]
    public async Task HeldContinuationRefusesPromptAdmissionBeforeBindingConfirmation()
    {
        using var directory = new TestDirectory();
        var entered = Signal(); var release = Signal(); var sends = 0;
        var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/prompt_async"))
            { Interlocked.Increment(ref sends); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return StubHttpMessageHandler.Json(Session("ses_owned", directory.Root));
        });
        var events = new GeneratedEvents("parts", false); events.ReleaseDisposal.TrySetResult();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, new Uri("http://127.0.0.1:4970"), eventStreamService: events));
        var session = await service.CreateAndConfirmSessionAsync(new() { Directory = directory.Root, Model = "model" });
        var pending = service.ContinueSessionAsync(session.Id, Binding("account-a"));
        try
        {
            await entered.Task.WaitAsync(Wait);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTurnAsync(session.Id,
                new() { MessageId = "msg_owned", Prompt = "Synthetic input", Model = "model" }));
            Assert.Equal(0, sends);
            release.TrySetResult(); await pending.WaitAsync(Wait);
            Assert.Equal(session.Id, (await service.ContinueSessionAsync(session.Id, Binding("account-a"))).Id);
        }
        finally { release.TrySetResult(); try { await pending.WaitAsync(Wait); } catch (InvalidOperationException) { } }
    }

    private static async Task<string?> Scalar(TestDatabase database, string sql)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static SessionBinding Binding(string account) => new(BackendType.OpenCode, "profile", account, "model", null, null, null);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Destination(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query["?directory=".Length..]);
    private static string Session(string id, string directory) => JsonSerializer.Serialize(new
    {
        id, slug = id, projectID = "owned-project", directory, title = "Synthetic session", version = "1.18.31",
        model = new { id = "model", providerID = "provider" }, time = new { created = 1, updated = 1 }
    });

    private sealed class GeneratedEvents(string dimension, bool overflow) : IOpenCodeEventStreamService
    {
        public bool IsOverflowed => false;
        public string? SpoolFilePath => null;
        public TaskCompletionSource Disposing { get; } = Signal();
        public TaskCompletionSource ReleaseDisposal { get; } = Signal();
        public bool Disposed { get; private set; }
        public IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null) => [];

        public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(string? sessionId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return Event("server.connected", new { });
                if (dimension != "messages") yield return Assistant("msg_assistant", false);
                if (dimension is "error" or "finish" or "blank-error" or "empty-error")
                {
                    var metadata = new string('x', (dimension == "finish" ? 4096 : 4 * 1024 * 1024) + (overflow ? 1 : 0));
                    yield return Event("message.updated", new
                    {
                        info = new { id = "msg_assistant", sessionID = "ses_owned", role = "assistant", parentID = "msg_owned",
                            finish = dimension == "finish" ? metadata : null,
                            error = dimension == "error" ? metadata : dimension == "blank-error" ? " " : dimension == "empty-error" ? "" : null,
                            time = new { } }
                    });
                    if (dimension is "error" or "finish")
                    { yield return Assistant("msg_assistant", true); yield break; }
                }

                var count = 4096 + (overflow && dimension != "text" ? 1 : 0);
                var text = dimension == "text" ? new string('x', 1024) : "x";
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (dimension == "messages") { yield return Assistant("msg_" + index, false); continue; }
                    yield return Event("message.part.updated", new
                    {
                        part = new
                        {
                            id = "synthetic-private-part-" + index, sessionID = "ses_owned", messageID = "msg_assistant",
                            type = dimension is "tools" or "empty-tool-id" ? "tool" : "text", text,
                            callID = dimension == "empty-tool-id" ? "" : "call-" + index, tool = "synthetic-tool", state = new { status = "completed" }
                        }
                    });
                }
                if (overflow && dimension == "text")
                    yield return Event("message.part.delta", new
                    {
                        sessionID = "ses_owned", partID = "synthetic-private-part-0", messageID = "msg_assistant", field = "text", delta = "x"
                    });
                // Reuse an already retained message so the exact4096-record control stays inclusive.
                yield return Assistant(dimension == "messages" ? "msg_0" : "msg_assistant", true);
            }
            finally
            {
                Disposing.TrySetResult();
                await ReleaseDisposal.Task.ConfigureAwait(false);
                Disposed = true;
            }
        }

        private static OpenCodeEventEnvelope Assistant(string id, bool completed) => Event("message.updated", new
        {
            info = new { id, sessionID = "ses_owned", role = "assistant", parentID = "msg_owned",
                finish = completed ? "stop" : null, time = new { completed = completed ? (long?)1 : null } }
        });
        private static OpenCodeEventEnvelope Event(string type, object properties)
        {
            var json = JsonSerializer.SerializeToElement(properties);
            return new(type, json, "Synthetic bounded frame", DateTime.UtcNow);
        }
    }
}

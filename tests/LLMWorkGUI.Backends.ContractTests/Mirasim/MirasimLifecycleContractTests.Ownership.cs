using System.Net;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed partial class MirasimLifecycleContractTests
{
    [Theory]
    [InlineData("turn-done-with-error-sample.json")]
    [InlineData("turn-incomplete-sample.json")]
    [InlineData("turn-cancel-response-sample.json")]
    public async Task HistoricalFixtureWithoutIdentity_RemainsAmbiguous(string name)
    {
        var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, File.ReadAllText(FixturePath(name)))));
        var locks = new FakeCheckoutLockService();
        var result = await CreateService(handler, locks).ExecuteTurnAsync(CreateRequest());
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.True(locks.Token.IsHeld);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingEchoCannotReleaseTheRequestedTurn(bool missingSession)
    {
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { turnId = TurnId, sessionKey = SessionKey, phase = "running", model = ModelId })
                : missingSession ? Json(new { turnId = TurnId, phase = "done", terminal = true, model = ModelId })
                : Json(new { sessionKey = SessionKey, phase = "done", terminal = true, model = ModelId }))));
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks);
        await service.ExecuteTurnAsync(CreateRequest());
        var result = await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MismatchedEchoCannotReleaseTheRequestedTurn(bool cancel, bool wrongSession)
    {
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId })
                : Json(new { turnId = wrongSession ? TurnId : "foreign-turn",
                    sessionKey = wrongSession ? "foreign-session" : SessionKey,
                    phase = cancel ? "cancelled" : "done", terminal = true, model = ModelId }))));
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks);
        await service.ExecuteTurnAsync(CreateRequest());
        var result = cancel ? await service.CancelTurnAsync(SessionKey, TurnId)
            : await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task ConnectionErrorAfterPossibleDispatchRetainsOwnership()
    {
        var handler = new RecordingHttpMessageHandler((_, _) => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Synthetic connection loss after delivery"));
        var locks = new FakeCheckoutLockService();
        var result = await CreateService(handler, locks).ExecuteTurnAsync(CreateRequest());
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task TerminalDispatchReleaseFailureCanBeRetriedForTheExactTurn()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, DoneJson())));
        var locks = new FakeCheckoutLockService();
        locks.Token.ReleaseHandler = () => Task.FromException(new IOException("Synthetic database release failure"));
        var service = CreateService(handler, locks);
        var result = await service.ExecuteTurnAsync(CreateRequest());
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.True(locks.Token.IsHeld);
        locks.Token.ReleaseHandler = null;
        await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(1, locks.Token.ReleaseCount);
        Assert.False(locks.Token.IsHeld);
    }

    [Fact]
    public async Task ConcurrentTerminalReleasesWaitForTheFailedAttemptAndRetry()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId }) : DoneJson())));
        var locks = new FakeCheckoutLockService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        locks.Token.ReleaseHandler = async () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await release.Task;
                throw new IOException("Synthetic first release failure");
            }
        };
        var service = CreateService(handler, locks);
        await service.ExecuteTurnAsync(CreateRequest());
        var first = service.ReconcileTurnAsync(SessionKey, TurnId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.False(second.IsCompleted);
        Assert.True(locks.Token.IsHeld);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(1, locks.Token.ReleaseCount);
    }
}

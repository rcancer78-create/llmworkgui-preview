using System.Net;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Mirasim;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed partial class MirasimLifecycleContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitContradictoryAccountOrLegCannotBecomeCompleted(bool reconcile, bool wrongLeg)
    {
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            reconcile && request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId })
                : DoneJson(account: wrongLeg ? "own" : "foreign", leg: wrongLeg ? "foreign" : "own"))));
        using var service = CreateService(handler);
        var result = await service.ExecuteTurnAsync(CreateRequest());
        if (reconcile) result = await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(MirasimTurnStatus.Failed, result.Status);
        Assert.Equal(MirasimTurnErrorClass.RouteMismatch, result.ErrorClass);
    }

    // Synthetic peer responses exercise local parsing/ownership, not native execution evidence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedTerminalResponseCannotReleaseUnknownWriter(bool reconcile)
    {
        var large = DoneJson(response: new string('a', 4 * 1024 * 1024 + 100));
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            reconcile && request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId })
                : large)));
        var locks = new FakeCheckoutLockService();
        using var service = CreateService(handler, locks);
        var result = await service.ExecuteTurnAsync(CreateRequest());
        if (reconcile) result = await service.ReconcileTurnAsync(SessionKey, TurnId);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
        Assert.Null(result.AssistantResponse);
    }

    [Fact]
    public async Task OversizedStreamLineIsRejectedWithoutReleasingRunningWriter()
    {
        var handler = new RecordingHttpMessageHandler((request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("/turns", StringComparison.Ordinal)
                ? Json(new { sessionKey = SessionKey, turnId = TurnId, phase = "running", model = ModelId })
                : new string('a', 1024 * 1024 + 100) + "\n")));
        var locks = new FakeCheckoutLockService();
        using var service = CreateService(handler, locks);
        Assert.Equal(MirasimTurnStatus.Running, (await service.ExecuteTurnAsync(CreateRequest())).Status);
        await Assert.ThrowsAsync<MirasimClientException>(async () =>
        {
            await foreach (var _ in service.WatchTurnAsync(SessionKey, TurnId)) { }
        });
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotBypassActualOperationDrain()
    {
        using var lifetime = new MirasimLifecycleHostedService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("Synthetic callback failure"));
            entered.TrySetResult();
            await release.Task;
            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });
        using var service = CreateService(handler, lifetime: lifetime);
        var sending = service.ExecuteTurnAsync(CreateRequest());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = lifetime.StopAsync(CancellationToken.None);
        try
        {
            await Task.WhenAny(stopping, Task.Delay(100));
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}

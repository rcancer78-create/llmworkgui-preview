using System.Net;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed partial class MirasimLifecycleContractTests
{
    [Fact]
    public async Task ModelTurnMayOutlastControlRequestBudgetWithoutBecomingUncertain()
    {
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromMilliseconds(50),
            turnHardTimeout: TimeSpan.FromSeconds(2));
        var result = await service.ExecuteTurnAsync(CreateRequest());
        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.True(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(1, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task TurnHardDeadlineStillPreservesUncertainDeliveryAndDoesNotResend()
    {
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromSeconds(2),
            turnHardTimeout: TimeSpan.FromMilliseconds(50));
        var result = await service.ExecuteTurnAsync(CreateRequest());
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.Equal(MirasimTurnErrorClass.Timeout, result.ErrorClass);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task CallerCancellationAfterDeliveryStillRetainsWriterUntilTerminalEvidence()
    {
        using var caller = new CancellationTokenSource();
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            caller.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromMilliseconds(50),
            turnHardTimeout: TimeSpan.FromSeconds(2));
        var result = await service.ExecuteTurnAsync(CreateRequest(), caller.Token);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295L)]
    public void InvalidModelTurnDeadlineIsRejectedBeforeAnyRequest(long milliseconds)
    {
        var options = new MirasimOptions { TurnHardTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}

using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

public sealed class HealthCenterNotificationTests
{
    [Fact]
    public async Task ThrowingSubscriber_DoesNotFailCommittedTransitionOrSkipOtherSubscribers()
    {
        var states = new InMemoryHealthStateRepository();
        var events = new InMemoryHealthEventRepository();
        var service = new HealthCenterService(states, events, new HealthTestTimeProvider());
        var notified = 0;
        service.TransitionRecorded += (_, _) => throw new InvalidOperationException("Subscriber fixture failure.");
        service.TransitionRecorded += (_, _) => notified++;

        var result = await service.ReportFailureAsync(HealthScope.ForRoute("event-route"), HealthErrorClass.NetworkOrTimeout);

        Assert.Equal(HealthState.Degraded, result.Snapshot.State);
        Assert.Single(events.Events);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotPreventAuthenticationCascade()
    {
        var states = new InMemoryHealthStateRepository();
        var events = new InMemoryHealthEventRepository();
        var service = new HealthCenterService(states, events, new HealthTestTimeProvider());
        var account = HealthScope.ForAccount("event-account");
        var route = HealthScope.ForModelRoute(account.ScopeId, "model-one");
        await service.ReportSuccessAsync(route);
        service.TransitionRecorded += (_, _) => throw new InvalidOperationException("Subscriber fixture failure.");

        var result = await service.ReportFailureAsync(account, HealthErrorClass.AuthenticationOrRefresh);

        Assert.False(result.Snapshot.IsRoutable);
        Assert.False((await service.GetSnapshotAsync(route)).IsRoutable);
        Assert.Contains(events.Events, entry => entry.ScopeType == route.ScopeType && entry.ScopeId == route.ScopeId);
    }

    [Fact]
    public async Task Subscriber_CanSynchronouslyIssueAnotherHealthCommandWithoutDeadlock()
    {
        var states = new InMemoryHealthStateRepository();
        var events = new InMemoryHealthEventRepository();
        var service = new HealthCenterService(states, events, new HealthTestTimeProvider());
        var scope = HealthScope.ForRoute("reentrant-event-route");
        var callbacks = 0;
        service.TransitionRecorded += (_, _) =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                // This deliberately models a synchronous EventHandler subscriber. The inner bound
                // detects an accidentally held transition gate without leaving a blocked test worker.
#pragma warning disable xUnit1031
                service.DisableManuallyAsync(scope, "Disabled by subscriber.")
                    .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
        };

        await Task.Run(() => service.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthState.DisabledManual, (await service.GetSnapshotAsync(scope)).State);
        Assert.Equal(2, callbacks);
    }
}

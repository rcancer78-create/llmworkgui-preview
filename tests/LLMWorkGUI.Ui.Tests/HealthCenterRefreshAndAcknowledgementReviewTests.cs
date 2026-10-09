using System.IO;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class HealthCenterRefreshAndAcknowledgementReviewTests
{
    private static readonly HealthScope Scope = HealthScope.ForAccount("owned-health-scope");

    [Theory]
    [InlineData("list")]
    [InlineData("fanout")]
    public async Task FailedRefreshPreservesObservedRowsButShowsFailureInTheScreenAndStatusBar(string failingStage)
    {
        var states = new HeldFailureStateStore();
        var events = new InMemoryHealthEventStore();
        var fanout = new HeldFailureFanoutStore(states, events);
        var time = new HealthUiTimeProvider();
        var service = new HealthCenterService(states, events, time, new HealthPolicy { FailureThreshold = 1 },
            transitionStore: fanout);
        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
        var attempt = await service.TryBeginProbeAttemptAsync(Scope, true, "owned previously observed model probe");
        Assert.NotNull(attempt);
        await service.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded, "owned observed completion");
        var cli = new CliStatusViewModel(FakeCliDetectionService.Degraded(), time);
        var status = new StatusBarViewModel(cli, healthCenter: service);
        var model = new HealthCenterViewModel(cli, service, statusBar: status);
        await model.RefreshAsync();
        var observed = Assert.Single(model.Scopes);
        Assert.Equal(HealthState.Healthy, observed.State);
        var previousIndicator = model.HealthIndicator;
        var previousSummary = model.HealthSummary;
        var failure = new IOException("token=owned-private-health-repository-canary");
        if (failingStage == "list") states.ListFailure = failure;
        else fanout.PendingFailure = failure;

        var thrown = await Record.ExceptionAsync(model.RefreshAsync);

        Assert.Null(thrown);
        Assert.True(model.HasBlocker);
        Assert.DoesNotContain("owned-private-health-repository-canary", model.Blocker, StringComparison.Ordinal);
        Assert.Same(observed, Assert.Single(model.Scopes));
        Assert.False(model.IsBusy);
        Assert.NotEqual(previousIndicator, model.HealthIndicator);
        Assert.NotEqual(previousSummary, model.HealthSummary);
        Assert.Equal(model.HealthIndicator, status.HealthIndicator);
        Assert.Equal(model.HealthSummary, status.HealthDetail);
        states.ListFailure = null; fanout.PendingFailure = null;
        await model.RefreshAsync();
        Assert.False(model.HasBlocker);
        Assert.Equal(previousIndicator, model.HealthIndicator);
        Assert.Equal(previousSummary, model.HealthSummary);
    }

    [Fact]
    public async Task AcknowledgingOneModelDoesNotAuthorizeTheDifferentModelSelectedAfterward()
    {
        var time = new HealthUiTimeProvider();
        var service = new HealthCenterService(new InMemoryHealthStateStore(), new InMemoryHealthEventStore(), time,
            new HealthPolicy { FailureThreshold = 1 });
        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
        var probe = new OwnedProbeTrap(service);
        var model = new HealthCenterViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), time), service, probe);
        await model.RefreshAsync();
        model.ModelIdToProbe = "owned-model-a";
        model.CostPreviewAcknowledged = true;
        Assert.True(model.CanProbeModel);
        model.ModelIdToProbe = "owned-model-b";

        Assert.False(model.CostPreviewAcknowledged);
        Assert.False(model.CanProbeModel);
        Assert.False(model.ProbeModelCommand.CanExecute(null));
        Assert.Empty(probe.AdmittedModels);
        model.CostPreviewAcknowledged = true;
        Assert.True(model.ProbeModelCommand.CanExecute(null));
        await model.ProbeModelAsync();
        Assert.Equal("owned-model-b", Assert.Single(probe.AdmittedModels));
    }

    private sealed class HeldFailureStateStore : IHealthStateRepository
    {
        public InMemoryHealthStateStore Inner { get; } = new();
        public Exception? ListFailure { get; set; }
        public Task UpsertAsync(HealthStateRecord state, CancellationToken token = default) => Inner.UpsertAsync(state, token);
        public Task<HealthStateRecord?> GetAsync(string type, string id, CancellationToken token = default) => Inner.GetAsync(type, id, token);
        public Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken token = default) =>
            ListFailure is null ? Inner.ListAsync(token) : Task.FromException<IReadOnlyList<HealthStateRecord>>(ListFailure);
    }

    private sealed class HeldFailureFanoutStore(HeldFailureStateStore states, InMemoryHealthEventStore events)
        : IHealthTransitionStore, IHealthAuthenticationFanoutStore
    {
        public Exception? PendingFailure { get; set; }
        public async Task SaveAsync(HealthStateRecord? state, HealthEventRecord? audit, CancellationToken token = default)
        {
            if (state is not null) await states.UpsertAsync(state, token);
            if (audit is not null) await events.AppendAsync(audit, token);
        }
        public Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken token) =>
            PendingFailure is null ? Task.FromResult<IReadOnlyList<HealthAuthenticationProjection>>([])
                : Task.FromException<IReadOnlyList<HealthAuthenticationProjection>>(PendingFailure);
        public Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken token) =>
            throw new InvalidOperationException("No authentication failure is manufactured by this UI refresh fixture.");
        public Task<bool> HasPendingAsync(HealthScope scope, CancellationToken token) => Task.FromResult(false);
        public async Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken token) =>
            (await states.GetAsync(scope.ScopeType, scope.ScopeId, token), false);
        public Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord audit, string projectionId, CancellationToken token) =>
            SaveAsync(state, audit, token);
    }

    private sealed class OwnedProbeTrap(IHealthCenterService service) : IHealthProbeService
    {
        public bool SupportsModelProbe => true;
        public List<string> AdmittedModels { get; } = new();
        public Task<HealthProbeOutcome> ProbeConnectionAsync(HealthScope scope, CancellationToken token = default) =>
            throw new InvalidOperationException("This fixture must never invoke a connection probe.");
        public async Task<HealthProbeOutcome> ProbeModelAsync(HealthScope scope, HealthProbeConfirmation confirmation, CancellationToken token = default)
        {
            if (confirmation.CostPreviewAcknowledged) AdmittedModels.Add(confirmation.ModelId);
            return new HealthProbeOutcome
            {
                Scope = scope, Kind = HealthProbeKind.Model, Succeeded = false,
                Refusal = HealthProbeRefusal.ConfirmationRequired,
                Snapshot = await service.GetSnapshotAsync(scope, token), Explanation = "Owned model-probe caller trap; no provider is contacted."
            };
        }
    }
}

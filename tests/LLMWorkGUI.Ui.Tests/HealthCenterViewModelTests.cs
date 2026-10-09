using System.Linq;
using System.IO;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the Health Center screen. The view model is driven by the real
/// <see cref="HealthCenterService"/> over in-memory stores, so the assertions are about what the
/// operator is actually shown and allowed to do, not about a hand-written stub.
/// </summary>
public sealed class HealthCenterViewModelTests
{
    [Fact]
    public async Task RefreshPersistenceFailureShowsSafeBlockerAndStillReadsRecoveringScope()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);
        await service.TryBeginProbeAttemptAsync(AccountScope, true, "observed work awaiting persistence");
        var probe = new StubHealthProbeService(service)
        {
            RepairPending = () => throw new IOException("token=synthetic-private-probe-persistence-value")
        };
        var viewModel = CreateViewModel(service, probe);

        await viewModel.RefreshAsync();

        Assert.Equal(1, probe.RepairCalls);
        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("synthetic-private-probe-persistence-value", viewModel.Blocker, StringComparison.Ordinal);
        Assert.Equal(HealthState.Recovering, Assert.Single(viewModel.Scopes).State);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task RefreshRepairsRetainedProbeCompletionBeforeReadingScopes()
    {
        var service = CreateService();
        await MoveToProbeRequiredAsync(service);
        var attempt = await service.TryBeginProbeAttemptAsync(AccountScope, true, "already observed provider work");
        Assert.NotNull(attempt);
        var probe = new StubHealthProbeService(service)
        {
            RepairPending = async () =>
            {
                await service.CompleteProbeAttemptAsync(attempt!, HealthProbeCompletionKind.ModelSucceeded,
                    "saving the retained observation without another provider call");
            }
        };
        var viewModel = CreateViewModel(service, probe);

        await viewModel.RefreshAsync();

        Assert.Equal(1, probe.RepairCalls);
        Assert.Equal(HealthState.Healthy, Assert.Single(viewModel.Scopes).State);
        Assert.False(viewModel.CanProbeModel);
    }

    [Fact]
    public async Task LateAuditForOldSelection_DoesNotReplaceCurrentScopeAudit()
    {
        await Task.Run(async () =>
        {
            var events = new HeldAuditRepository();
            var service = new HealthCenterService(new InMemoryHealthStateStore(), events);
            var first = HealthScope.ForAccount("first");
            var second = HealthScope.ForAccount("second");
            await service.ReportFailureAsync(first, HealthErrorClass.NetworkOrTimeout);
            await service.ReportFailureAsync(second, HealthErrorClass.NetworkOrTimeout);
            var oldEntries = await events.Inner.ListByScopeAsync(first.ScopeType, first.ScopeId);
            events.HeldScopeId = first.ScopeId;
            var viewModel = CreateViewModel(service);
            viewModel.SelectedScope = new HealthScopeViewModel(await service.GetSnapshotAsync(first));
            viewModel.SelectedScope = new HealthScopeViewModel(await service.GetSnapshotAsync(second));
            Assert.Equal(second, Assert.Single(viewModel.Audit).Entry.Scope);

            events.Release.SetResult(oldEntries);

            Assert.Equal(second, Assert.Single(viewModel.Audit).Entry.Scope);
        });
    }

    [Fact]
    public async Task AuditReadFailure_IsObservedWithoutPublishingRawExceptionText()
    {
        await Task.Run(async () =>
        {
            var events = new HeldAuditRepository { HeldScopeId = AccountScope.ScopeId };
            var service = new HealthCenterService(new InMemoryHealthStateStore(), events);
            var viewModel = CreateViewModel(service);
            viewModel.SelectedScope = new HealthScopeViewModel(await service.GetSnapshotAsync(AccountScope));

            events.Release.SetException(new InvalidOperationException("token=synthetic-private-audit-value"));

            Assert.True(viewModel.HasBlocker);
            Assert.DoesNotContain("synthetic-private-audit-value", viewModel.Blocker, StringComparison.Ordinal);
            Assert.Empty(viewModel.Audit);
        });
    }

    [Fact]
    public async Task LateImpactedSessionsForOldSelection_DoNotReplaceCurrentSummary()
    {
        await Task.Run(async () =>
        {
            var service = CreateService();
            var first = HealthScope.ForAccount("first");
            var second = HealthScope.ForAccount("second");
            var impact = new HeldImpactService(first);
            var viewModel = CreateViewModel(service, impactedSessions: impact);
            viewModel.SelectedScope = new HealthScopeViewModel(await service.GetSnapshotAsync(first));
            viewModel.SelectedScope = new HealthScopeViewModel(await service.GetSnapshotAsync(second));
            Assert.Equal("second", viewModel.ImpactedSessionSummary);

            impact.Release.SetResult(HeldImpactService.Report(first));

            Assert.Equal("second", viewModel.ImpactedSessionSummary);
        });
    }

    private sealed class HeldAuditRepository : IHealthEventRepository
    {
        public InMemoryHealthEventStore Inner { get; } = new();
        public string? HeldScopeId { get; set; }
        // Deliberately inline: the test runs without a synchronization context, so release
        // completes the service and its publication before the next assertion.
        public TaskCompletionSource<IReadOnlyList<HealthEventRecord>> Release { get; } = new();
        public Task AppendAsync(HealthEventRecord entry, CancellationToken token = default) => Inner.AppendAsync(entry, token);
        public Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(int limit, CancellationToken token = default) =>
            Inner.ListRecentAsync(limit, token);
        public Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(string type, string id, int? limit = null,
            CancellationToken token = default) => id == HeldScopeId ? Release.Task : Inner.ListByScopeAsync(type, id, limit, token);
    }

    private sealed class HeldImpactService(HealthScope held) : IImpactedSessionService
    {
        public TaskCompletionSource<ImpactedSessionReport> Release { get; } = new();
        public Task<ImpactedSessionReport> GetImpactedSessionsAsync(HealthScope scope, CancellationToken token = default) =>
            scope == held ? Release.Task : Task.FromResult(Report(scope));
        public static ImpactedSessionReport Report(HealthScope scope) => new()
        {
            Scope = scope, State = HealthState.Healthy, IsRoutable = true, IsComplete = true, Summary = scope.ScopeId
        };
    }

    private static readonly HealthScope AccountScope = HealthScope.ForAccount("account-1");

    private readonly InMemoryHealthStateStore _states = new();
    private readonly InMemoryHealthEventStore _events = new();
    private readonly HealthUiTimeProvider _time = new();

    [Fact]
    public void WithoutTheService_TheScreenStatesItIsUnavailableInsteadOfClaimingHealth()
    {
        var viewModel = CreateViewModel(withService: false);

        Assert.False(viewModel.IsHealthCenterAvailable);

        // "Not reported" is the only honest indicator: nothing has been observed.
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, viewModel.HealthIndicator);
        Assert.Equal(viewModel.UnavailableNotice, viewModel.ScopeNote);
        Assert.Equal(viewModel.UnavailableNotice, viewModel.EmptyStateMessage);

        // Every recovery action is unavailable, so nothing can be recorded against a missing service.
        Assert.False(viewModel.CanStartProbe);
        Assert.False(viewModel.CanTestConnection);
        Assert.False(viewModel.CanProbeModel);
        Assert.False(viewModel.CanRunProbe);
        Assert.False(viewModel.CanDisable);
        Assert.False(viewModel.CanEnable);
        Assert.False(viewModel.CanForceEnable);
        Assert.False(viewModel.CanCheckCooldown);
    }

    [Fact]
    public async Task WithTheServiceButNoObservations_TheScreenShowsNoInventedScope()
    {
        var viewModel = CreateViewModel();

        await viewModel.RefreshAsync();

        // A healthy-by-default row would be a fabricated observation.
        Assert.False(viewModel.HasScopes);
        Assert.Empty(viewModel.Scopes);
        Assert.Null(viewModel.SelectedScope);
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, viewModel.HealthIndicator);
    }

    [Fact]
    public async Task ARepeatedFailure_IsShownAsExcludedFromRoutingAndAwaitingAProbe()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service);
        var viewModel = CreateViewModel(service, probe);

        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        await viewModel.RefreshAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.CoolingDown, scope.State);
        Assert.False(scope.IsRoutable);
        Assert.Equal("Исключён из маршрутизации", scope.RoutingDisplay);

        // Cooling down is the one state the operator can only move forward by re-checking the deadline.
        Assert.True(viewModel.CanCheckCooldown);
        Assert.False(viewModel.CanStartProbe);
        Assert.False(viewModel.CanTestConnection);

        _time.Advance(System.TimeSpan.FromMinutes(10));
        await viewModel.CheckCooldownAsync();

        var awaiting = Assert.Single(viewModel.Scopes);

        // ROADMAP Phase 7: an elapsed cooldown requires a probe and never returns to Healthy on its own.
        Assert.Equal(HealthState.ProbeRequired, awaiting.State);
        Assert.False(awaiting.IsRoutable);
        Assert.True(awaiting.RequiresProbe);
        Assert.True(viewModel.CanStartProbe);
        Assert.True(viewModel.CanTestConnection);
    }

    [Fact]
    public async Task AForcedRoute_IsNeverPresentedAsARecoveredOne()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();
        await viewModel.ForceEnableAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.ForcedEnabled, scope.State);
        Assert.True(scope.IsRoutable);
        Assert.True(scope.IsForcedWithoutVerification);
        Assert.Contains("не подтверждено", scope.ForcedWarning, System.StringComparison.OrdinalIgnoreCase);

        // The screen summary must name the forced route instead of counting it as plain routable.
        Assert.Contains("принудительно без проверки", viewModel.HealthIndicator, System.StringComparison.Ordinal);

        var entry = viewModel.Audit.First();

        Assert.True(entry.IsForcedRecovery);
        Assert.False(entry.IsVerifiedRecovery);
        Assert.Equal("Принудительно, без проверки", entry.RecoveryKindDisplay);

        // A forced route is not awaiting a probe, and no operator-judgement "pass" button exists to
        // claim one: the audit can only ever classify this transition as forced and unverified.
        Assert.False(viewModel.CanStartProbe);
        Assert.False(viewModel.CanTestConnection);
        Assert.False(viewModel.CanProbeModel);
    }

    [Fact]
    public async Task APassingProbe_IsTheOnlyPathShownAsAVerifiedRecovery()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service);
        var viewModel = CreateViewModel(service, probe);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();

        // The connection probe is a measurement, but it confirms only the endpoint.
        await viewModel.RunProbeAsync();

        var probing = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.Recovering, probing.State);
        Assert.False(probing.IsRoutable);
        Assert.False(viewModel.Audit.First().IsVerifiedRecovery);

        // Only the confirmed pinned model probe may claim the verified recovery.
        viewModel.ModelIdToProbe = "mock-model";
        viewModel.CostPreviewAcknowledged = true;

        Assert.True(viewModel.CanProbeModel);

        await viewModel.RunProbeAsync();

        var recovered = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.Healthy, recovered.State);
        Assert.True(recovered.IsRoutable);
        Assert.False(recovered.IsForcedWithoutVerification);

        var entry = viewModel.Audit.First();

        Assert.True(entry.IsVerifiedRecovery);
        Assert.Equal("Подтверждённое восстановление", entry.RecoveryKindDisplay);
    }

    [Fact]
    public async Task AFailingProbe_IsShownAsQuarantinedRatherThanRestored()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service) { Succeeds = false };
        var viewModel = CreateViewModel(service, probe);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();
        await viewModel.RunProbeAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.QuarantinedAuto, scope.State);
        Assert.False(scope.IsRoutable);
        Assert.True(scope.RequiresProbe);

        // A quarantined scope is not awaiting a probe start any more, so no probe is offered from here;
        // only an explicit operator decision may force it back into routing.
        Assert.False(viewModel.CanStartProbe);
        Assert.False(viewModel.CanTestConnection);
        Assert.True(viewModel.CanForceEnable);
    }

    [Fact]
    public async Task AnUnobservedField_IsRenderedAsNotReportedRatherThanAsAZeroValue()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        await service.DisableManuallyAsync(AccountScope, "Disabled while the provider migrates.");
        await viewModel.RefreshAsync();

        var scope = Assert.Single(viewModel.Scopes);

        // A manual disable records no error class and no cooldown; neither may be invented.
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, scope.ErrorClassDisplay);
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, scope.CooldownDisplay);

        var entry = viewModel.Audit.First();

        // The first observation of a scope has no previous state, which is not the same as Healthy.
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, entry.PreviousStateDisplay);
        Assert.Equal("Disabled while the provider migrates.", entry.ReasonDisplay);
        Assert.Equal("Переход", entry.RecoveryKindDisplay);
    }

    [Fact]
    public async Task ReEnablingAManuallyDisabledScope_RequiresAProbeInsteadOfBecomingHealthy()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        await service.DisableManuallyAsync(AccountScope, "Temporarily disabled.");
        await viewModel.RefreshAsync();

        Assert.True(viewModel.CanEnable);
        Assert.False(viewModel.CanDisable);

        await viewModel.EnableAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.ProbeRequired, scope.State);
        Assert.False(scope.IsRoutable);
    }

    [Fact]
    public async Task ARejectedAction_SurfacesAsABlockerAndLeavesTheObservedStateIntact()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service);
        var viewModel = CreateViewModel(service, probe);

        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        await viewModel.RefreshAsync();

        // Starting a probe while cooling down is rejected by the state machine. The screen must report
        // the refusal instead of showing a probe that never started.
        await viewModel.TestConnectionAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Equal(HealthState.CoolingDown, Assert.Single(viewModel.Scopes).State);
    }

    [Fact]
    public async Task ARefresh_KeepsTheOperatorOnTheSameSelectedScope()
    {
        var service = CreateService();
        var viewModel = CreateViewModel(service);

        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        await service.DisableManuallyAsync(HealthScope.ForBackend("backend-1"), "Manually disabled.");
        await viewModel.RefreshAsync();

        viewModel.SelectedScope = viewModel.Scopes.Single(scope => scope.Key == "backend:backend-1");

        await viewModel.RefreshAsync();

        Assert.Equal("backend:backend-1", viewModel.SelectedScope!.Key);
    }

    [Fact]
    public void WithoutAProbeExecutor_TheScreenSaysNoVerifiedRecoveryIsPossible()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsProbeExecutionAvailable);
        Assert.False(viewModel.CanRunProbe);

        // The operator must be told that no pinned model probe is possible here, so the route can never
        // be shown as recovered by observation.
        Assert.Contains(
            "не настроен исполнитель закреплённой проверки модели",
            viewModel.ProbeExecutionNote,
            System.StringComparison.Ordinal);
        Assert.False(viewModel.HasProbeResult);
    }

    [Fact]
    public async Task RunProbe_OnAPassingProbe_ShowsTheObservedResultAndVerifiesTheRecovery()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service);
        var viewModel = CreateViewModel(service, probe);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();

        // The pinned model probe is the only probe that may verify a recovery, so it is the one the
        // operator confirms with a cost preview.
        viewModel.ModelIdToProbe = "mock-model";
        viewModel.CostPreviewAcknowledged = true;

        Assert.True(viewModel.IsProbeExecutionAvailable);
        Assert.True(viewModel.CanRunProbe);
        Assert.True(viewModel.CanProbeModel);

        await viewModel.RunProbeAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.Healthy, scope.State);
        Assert.True(scope.IsRoutable);

        // The observed result is shown, and no blocker was raised.
        Assert.True(viewModel.HasProbeResult);
        Assert.False(viewModel.HasBlocker);
        Assert.True(viewModel.Audit.First().IsVerifiedRecovery);
    }

    [Fact]
    public async Task RunProbe_OnAFailingProbe_ShowsTheObservedFailureAndQuarantines()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service) { Succeeds = false };
        var viewModel = CreateViewModel(service, probe);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();
        await viewModel.RunProbeAsync();

        var scope = Assert.Single(viewModel.Scopes);

        Assert.Equal(HealthState.QuarantinedAuto, scope.State);
        Assert.False(scope.IsRoutable);

        // A failing probe is a real observed result, not a blocker.
        Assert.True(viewModel.HasProbeResult);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public async Task RunProbe_WhenTheProbeIsRefused_ShowsABlockerAndLeavesTheStateUntouched()
    {
        var service = CreateService();
        var probe = new StubHealthProbeService(service)
        {
            Refusal = HealthProbeRefusal.ProbeTargetHasNoEndpoint
        };
        var viewModel = CreateViewModel(service, probe);

        await MoveToProbeRequiredAsync(service);
        await viewModel.RefreshAsync();
        await viewModel.RunProbeAsync();

        var scope = Assert.Single(viewModel.Scopes);

        // "Could not check" must never be shown as "the check failed".
        Assert.Equal(HealthState.ProbeRequired, scope.State);
        Assert.True(viewModel.HasBlocker);
        Assert.False(viewModel.HasProbeResult);
    }

    [Fact]
    public void WithoutTheImpactedSessionService_TheSectionIsNotOffered()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsImpactedSessionViewAvailable);
        Assert.False(viewModel.HasImpactedSessions);
        Assert.Empty(viewModel.ImpactedSessionSummary);
    }

    [Fact]
    public async Task AStoppedTurn_IsShownAsRequiringAReplacementSession()
    {
        var service = CreateService();
        var sessions = new InMemorySessionStore();
        var viewModel = CreateViewModel(service, impactedSessions: new ImpactedSessionService(service, sessions));

        sessions.Add("session-running", SessionState.Active, activeExecutionId: "exec-live");
        sessions.Add("session-waiting", SessionState.Idle);

        await service.ReportFailureAsync(AccountScope, HealthErrorClass.Provider4xx5xx);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsImpactedSessionViewAvailable);
        Assert.True(viewModel.HasImpactedSessions);
        Assert.Equal(2, viewModel.ImpactedSessions.Count);

        var stopped = viewModel.ImpactedSessions.Single(session => session.SessionIdDisplay == "session-running");

        Assert.True(stopped.RequiresReplacementSession);
        Assert.True(stopped.HasTurnInFlight);
        Assert.Equal(
            "Выполняемый ход должен быть остановлен — замена сессии требует вашего подтверждения",
            stopped.ImpactDisplay);

        var blocked = viewModel.ImpactedSessions.Single(session => session.SessionIdDisplay == "session-waiting");

        Assert.False(blocked.RequiresReplacementSession);
        Assert.Equal("Следующий ход заблокирован", blocked.ImpactDisplay);

        Assert.Contains("1 mid-turn with a running turn that must be stopped", viewModel.ImpactedSessionSummary, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthyScope_ShowsNoImpactedSessionButStillExplainsTheLookup()
    {
        var service = CreateService();
        var sessions = new InMemorySessionStore();
        var viewModel = CreateViewModel(service, impactedSessions: new ImpactedSessionService(service, sessions));

        sessions.Add("session-ok", SessionState.Active, activeExecutionId: "exec-1");

        // Observed, but not excluded from routing: a manual disable of a different scope.
        await service.DisableManuallyAsync(HealthScope.ForBackend("backend-1"), "Manually disabled.");
        await service.ReportFailureAsync(AccountScope, HealthErrorClass.UserCancellation);
        await viewModel.RefreshAsync();

        viewModel.SelectedScope = viewModel.Scopes.Single(scope => scope.Key == $"account:{AccountScope.ScopeId}");

        // A user cancellation does not move the breaker, so the route is still routable.
        Assert.False(viewModel.HasImpactedSessions);
        Assert.Contains("No session is affected", viewModel.ImpactedSessionSummary, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnresolvableScope_StatesTheLookupIsIncompleteRatherThanShowingNothing()
    {
        var service = CreateService();
        var sessions = new InMemorySessionStore();
        var viewModel = CreateViewModel(service, impactedSessions: new ImpactedSessionService(service, sessions));

        sessions.Add("session-1", SessionState.Active, activeExecutionId: "exec-1");

        // A backend scope cannot be resolved to sessions; the screen must say so.
        await service.DisableManuallyAsync(HealthScope.ForBackend("opencode-server"), "Manually disabled.");
        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasImpactedSessions);
        Assert.Contains(
            "cannot be resolved to sessions",
            viewModel.ImpactedSessionSummary,
            System.StringComparison.Ordinal);
    }

    /// <summary>Drives a scope to <see cref="HealthState.ProbeRequired"/> through observed steps only.</summary>
    private async Task MoveToProbeRequiredAsync(HealthCenterService service)
    {
        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(System.TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(AccountScope);
    }

    private HealthCenterViewModel CreateViewModel(
        IHealthCenterService? healthCenter = null,
        IHealthProbeService? probeService = null,
        IImpactedSessionService? impactedSessions = null,
        bool withService = true)
    {
        var service = withService ? healthCenter ?? CreateService() : null;

        return new HealthCenterViewModel(
            new CliStatusViewModel(FakeCliDetectionService.Degraded(), new FakeTimeProvider()),
            service,
            probeService,
            impactedSessions);
    }

    private HealthCenterService CreateService(HealthPolicy? policy = null) =>
        new(_states, _events, _time, policy ?? new HealthPolicy { FailureThreshold = 1 });
}

/// <summary>
/// Probe double that drives the real <see cref="HealthCenterService"/>, so the view model is exercised
/// against genuine state transitions rather than a hand-made snapshot.
/// </summary>
internal sealed class StubHealthProbeService : IHealthProbeService
{
    private readonly IHealthCenterService _healthCenter;

    public StubHealthProbeService(IHealthCenterService healthCenter)
    {
        _healthCenter = healthCenter;
    }

    public bool Succeeds { get; set; } = true;

    public int RepairCalls { get; private set; }
    public Func<Task>? RepairPending { get; set; }

    public async Task<int> RetryPendingCompletionsAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        RepairCalls++;
        if (RepairPending is not null) await RepairPending();
        return 0;
    }

    public bool SupportsModelProbe { get; set; } = true;

    public HealthProbeRefusal Refusal { get; set; } = HealthProbeRefusal.None;

    public async Task<HealthProbeOutcome> ProbeConnectionAsync(
        HealthScope scope,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (Refusal != HealthProbeRefusal.None)
        {
            return await RefuseAsync(scope, HealthProbeKind.Connection, cancellationToken);
        }

        await _healthCenter.StartProbeAsync(
            scope,
            "A connection probe was executed by the test double.",
            cancellationToken);

        // A passing connection probe is recorded as an observation only: it never leaves Recovering, so
        // it can never fabricate a verified recovery (ТЗ §6.10).
        var snapshot = await _healthCenter.RecordProbeObservationAsync(
            scope,
            Succeeds,
            Succeeds
                ? "The connection was confirmed, but a connection probe does not verify a recovery."
                : "The connection probe failed, so the scope was quarantined.",
            """{"probe":"providerConnection","status":"Stub"}""",
            cancellationToken);

        return new HealthProbeOutcome
        {
            Scope = scope,
            Kind = HealthProbeKind.Connection,
            Succeeded = Succeeds,
            ConfirmsVerifiedRecovery = false,
            Snapshot = snapshot,
            ErrorClass = Succeeds ? null : HealthErrorClass.Provider4xx5xx,
            LatencyMs = 5,
            ProbedEndpoint = "https://provider.example/v1/models",
            Explanation = Succeeds
                ? "The connection probe reached https://provider.example/v1/models in 5 ms. This confirms " +
                  "the connection only; a pinned model probe is still required."
                : "The connection probe against https://provider.example/v1/models failed."
        };
    }

    public async Task<HealthProbeOutcome> ProbeModelAsync(
        HealthScope scope,
        HealthProbeConfirmation confirmation,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (!SupportsModelProbe)
        {
            var unsupported = await _healthCenter.GetSnapshotAsync(scope, cancellationToken);

            return new HealthProbeOutcome
            {
                Scope = scope,
                Kind = HealthProbeKind.Model,
                Succeeded = false,
                Refusal = HealthProbeRefusal.ProbeModelUnsupported,
                Snapshot = unsupported,
                Explanation = "This composition cannot run a pinned model probe, so no verified recovery is possible."
            };
        }

        if (Refusal != HealthProbeRefusal.None)
        {
            return await RefuseAsync(scope, HealthProbeKind.Model, cancellationToken);
        }

        // Start and completion stay two audited steps, and the start is only recorded while the scope
        // actually owes a probe.
        var attempt = await _healthCenter.TryBeginProbeAttemptAsync(scope, true,
            "A pinned model probe was admitted by the test double.", cancellationToken);
        if (attempt is null) return await RefuseAsync(scope, HealthProbeKind.Model, cancellationToken);
        var completion = await _healthCenter.CompleteProbeAttemptAsync(attempt,
            Succeeds ? HealthProbeCompletionKind.ModelSucceeded : HealthProbeCompletionKind.Failed,
            "Observed test probe result", """{"probe":"providerModel","status":"Stub"}""", cancellationToken);
        var snapshot = completion.Snapshot;

        return new HealthProbeOutcome
        {
            Scope = scope,
            Kind = HealthProbeKind.Model,
            Succeeded = Succeeds,
            ConfirmsVerifiedRecovery = completion.VerifiedRecovery,
            Snapshot = snapshot,
            ErrorClass = Succeeds ? null : HealthErrorClass.Provider4xx5xx,
            LatencyMs = 5,
            ProbedEndpoint = "https://provider.example/v1/chat/completions",
            Explanation = Succeeds
                ? $"The pinned model probe for '{confirmation.ModelId}' reached " +
                  "https://provider.example/v1/chat/completions in 5 ms and verified the recovery."
                : $"The pinned model probe for '{confirmation.ModelId}' failed."
        };
    }

    private async Task<HealthProbeOutcome> RefuseAsync(
        HealthScope scope,
        HealthProbeKind kind,
        System.Threading.CancellationToken cancellationToken)
    {
        // A refusal leaves the health state exactly as observed.
        var observed = await _healthCenter.GetSnapshotAsync(scope, cancellationToken);

        return new HealthProbeOutcome
        {
            Scope = scope,
            Kind = kind,
            Succeeded = false,
            Refusal = Refusal,
            Snapshot = observed,
            Explanation = $"The probe was refused: {Refusal}."
        };
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Status-bar health integration (ТЗ §7.3). Without a Health Center the status bar may only claim the
/// basic executable/process/protocol checks; with one it must show the highest-severity observed state
/// instead of a health claim nobody observed.
/// </summary>
public sealed class StatusBarViewModelTests
{
    private static readonly HealthScope AccountScope = HealthScope.ForAccount("account-status");
    private static readonly HealthScope RouteScope = HealthScope.ForRoute("route-status");

    private readonly InMemoryHealthStateStore _states = new();
    private readonly InMemoryHealthEventStore _events = new();
    private readonly HealthUiTimeProvider _time = new();

    [Fact]
    public void WithoutAHealthCenter_TheStatusBarStaysOnBasicHealthAndTheLegacyScopeNote()
    {
        var viewModel = CreateStatusBar(healthCenter: null);

        Assert.Equal(StatusBarViewModel.BasicHealthLabel, viewModel.HealthIndicator);
        Assert.Equal(StatusBarViewModel.HealthScopeNoteConst, viewModel.HealthScopeNote);

        // The detail remains CLI detection evidence, never a health-center claim.
        Assert.Contains("CLI бэкендов обнаружено", viewModel.HealthDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAHealthCenter_TheStatusBarDefaultsToHealthyAndTheActiveScopeNote()
    {
        var viewModel = CreateStatusBar(CreateService());

        Assert.Equal(StatusBarViewModel.HealthyHealthLabel, viewModel.HealthIndicator);
        Assert.Equal(StatusBarViewModel.HealthCenterReadyDetail, viewModel.HealthDetail);
        Assert.Equal(StatusBarViewModel.ActiveHealthScopeNote, viewModel.HealthScopeNote);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithACoolingDownScope_ShowsCoolingDown()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        await viewModel.RefreshHealthAsync();

        Assert.Equal("Кулдаун", viewModel.HealthIndicator);
        Assert.Contains("Областей здоровья: 1", viewModel.HealthDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithAProbeRequiredScope_ShowsProbeRequired()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        await MoveToProbeRequiredAsync(service);

        await viewModel.RefreshHealthAsync();

        // Cooldown expiry means "probe required", never "healthy again".
        Assert.Equal("Требуется проверка", viewModel.HealthIndicator);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithAQuarantinedScope_ShowsQuarantined()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        await MoveToProbeRequiredAsync(service);
        await service.CompleteProbeAsync(AccountScope, succeeded: false);

        await viewModel.RefreshHealthAsync();

        Assert.Equal("Карантин", viewModel.HealthIndicator);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithAManuallyDisabledScope_ShowsDisabled()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        await service.DisableManuallyAsync(RouteScope, "Disabled while the provider migrates.");

        await viewModel.RefreshHealthAsync();

        Assert.Equal("Отключено", viewModel.HealthIndicator);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithMixedScopes_ShowsTheHighestSeverityObservedState()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        // Disabled outranks Cooling down, and neither may be hidden by the other.
        await service.DisableManuallyAsync(RouteScope, "Disabled while the provider migrates.");
        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        await viewModel.RefreshHealthAsync();

        Assert.Equal("Отключено", viewModel.HealthIndicator);

        // Quarantine the account scope; it now outranks the disabled route.
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(AccountScope);
        await service.CompleteProbeAsync(AccountScope, succeeded: false);

        await viewModel.RefreshHealthAsync();

        Assert.Equal("Карантин", viewModel.HealthIndicator);
        Assert.Contains("Областей здоровья: 2", viewModel.HealthDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshHealthAsync_WithOnlyHealthyScopes_ShowsHealthy()
    {
        var service = CreateService();
        var viewModel = CreateStatusBar(service);

        // A recorded success is an observed Healthy scope, not an assumption.
        await service.ReportSuccessAsync(AccountScope);

        await viewModel.RefreshHealthAsync();

        Assert.Equal(StatusBarViewModel.HealthyHealthLabel, viewModel.HealthIndicator);
    }

    [Fact]
    public void UpdateHealth_UpdatesThePropertiesAndRaisesPropertyChanged()
    {
        var viewModel = CreateStatusBar(CreateService());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.UpdateHealth("Quarantined", "2 scopes observed.");

        Assert.Equal("Quarantined", viewModel.HealthIndicator);
        Assert.Equal("2 scopes observed.", viewModel.HealthDetail);
        Assert.Contains(nameof(StatusBarViewModel.HealthIndicator), changed);
        Assert.Contains(nameof(StatusBarViewModel.HealthDetail), changed);

        // A detail-less update keeps the previous explanation instead of blanking it.
        viewModel.UpdateHealth("Degraded");

        Assert.Equal("Degraded", viewModel.HealthIndicator);
        Assert.Equal("2 scopes observed.", viewModel.HealthDetail);
    }

    [Fact]
    public async Task HealthCenterRefresh_PushesTheObservedHealthToTheStatusBar()
    {
        var service = CreateService();
        var statusBar = CreateStatusBar(service);

        var healthCenter = new HealthCenterViewModel(
            CreateCliStatus(),
            service,
            probeService: null,
            impactedSessions: null,
            statusBar);

        await MoveToProbeRequiredAsync(service);
        await service.CompleteProbeAsync(AccountScope, succeeded: false);

        await healthCenter.RefreshAsync();

        // The status bar mirrors the screen's observed state, not the optimistic default.
        Assert.Equal(healthCenter.HealthIndicator, statusBar.HealthIndicator);
        Assert.Equal(healthCenter.HealthSummary, statusBar.HealthDetail);
        Assert.Contains("ожидают проверки", statusBar.HealthIndicator, StringComparison.Ordinal);
        Assert.NotEqual(StatusBarViewModel.HealthCenterReadyDetail, statusBar.HealthDetail);
    }

    /// <summary>Drives a scope to <see cref="HealthState.ProbeRequired"/> through observed steps only.</summary>
    private async Task MoveToProbeRequiredAsync(HealthCenterService service)
    {
        await service.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(AccountScope);
    }

    private StatusBarViewModel CreateStatusBar(IHealthCenterService? healthCenter) =>
        new(CreateCliStatus(), instanceGuard: null, healthCenter);

    private static CliStatusViewModel CreateCliStatus() =>
        new(FakeCliDetectionService.Degraded(), new FakeTimeProvider());

    private HealthCenterService CreateService(HealthPolicy? policy = null) =>
        new(_states, _events, _time, policy ?? new HealthPolicy { FailureThreshold = 1 });
}

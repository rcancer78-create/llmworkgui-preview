using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition-root evidence for the Cursor ACP panel. TASK-040 left "the panel is only rendered when
/// a <see cref="CursorWorkspaceViewModel"/> is injected into the window" as an open risk, so the
/// production registration is asserted here instead of being assumed.
/// </summary>
public sealed class CursorWorkspaceCompositionTests
{
    [Fact]
    public void AddAppUi_RegistersTheCursorPanel()
    {
        using var provider = UiTestHost.CreateProvider();

        var panel = provider.GetService<CursorWorkspaceViewModel>();

        Assert.NotNull(panel);
    }

    [Fact]
    public void AddAppUi_InjectsTheCursorPanelIntoTheWorkspaceScreen()
    {
        using var provider = UiTestHost.CreateProvider();

        var workspace = provider.GetRequiredService<WorkspaceViewModel>();

        // Without this the shipped XAML collapses the panel and the whole Cursor surface is
        // unreachable for a real user, no matter how green the view-model tests are.
        Assert.True(workspace.HasCursorPanel);
        Assert.NotNull(workspace.Cursor);
        Assert.Same(provider.GetRequiredService<CursorWorkspaceViewModel>(), workspace.Cursor);
    }

    [Fact]
    public void AddAppUi_InjectsTheHealthCenterServiceIntoTheHealthCenterScreen()
    {
        // The UI-only graph has no Infrastructure, so the service is absent and the screen must say so
        // rather than render a fabricated healthy scope.
        using var uiOnly = UiTestHost.CreateProvider();

        var screen = uiOnly.GetRequiredService<HealthCenterViewModel>();

        Assert.False(screen.IsHealthCenterAvailable);
        Assert.Equal(HealthCenterViewModel.UnavailableIndicator, screen.HealthIndicator);
        Assert.False(screen.CanStartProbe);
        Assert.False(screen.CanForceEnable);
    }

    [Fact]
    public void AddAppUi_ConsumesAnAvailableHealthCenterService()
    {
        // With a health service registered, the same screen must actually consume it: an optional
        // dependency that is never resolved would make every green view-model test meaningless.
        using var provider = UiTestHost.CreateProvider(
            healthCenter: new HealthCenterService(
                new InMemoryHealthStateStore(),
                new InMemoryHealthEventStore(),
                new HealthUiTimeProvider()));

        var screen = provider.GetRequiredService<HealthCenterViewModel>();

        Assert.True(screen.IsHealthCenterAvailable);
        Assert.NotEqual(HealthCenterViewModel.UnavailableIndicator, screen.ScopeNote);
    }

    [Fact]
    public void CursorPanel_WithoutTheBackend_ReportsUnavailableInsteadOfFakingASession()
    {
        // The Cursor backend lives in Infrastructure, which this UI-only graph does not register.
        // The panel must then degrade honestly rather than present an unusable-but-enabled surface.
        using var provider = UiTestHost.CreateProvider();

        var panel = provider.GetRequiredService<CursorWorkspaceViewModel>();

        Assert.False(panel.IsBackendAvailable);
        Assert.False(panel.CanStartBackend);
        Assert.False(panel.CanSendPrompt);
        Assert.Equal(CursorWorkspaceViewModel.BackendStoppedState, panel.BackendStateDisplay);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, panel.NativeSessionId);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, panel.ProtocolVersionDisplay);
        Assert.Empty(panel.StreamEvents);

        // An unconfigured panel must never claim a read-only mode: the lock stays required.
        Assert.True(panel.ModeRequiresWriterLock);
        Assert.False(panel.CanAcquireWriterLock);
    }

    [Fact]
    public void AddAppUi_WithoutProjectRepository_DoesNotThrowAndLeavesResolverNull()
    {
        // The UI layer composes on its own: with no application layer the resolver service is not
        // registered, and the panel must still resolve instead of failing at composition time.
        var uiOnlyServices = new ServiceCollection();
        uiOnlyServices.AddAppUi();

        using (var uiOnly = uiOnlyServices.BuildServiceProvider())
        {
            Assert.Null(uiOnly.GetService<OpenedProjectResolver>());
            Assert.NotNull(uiOnly.GetRequiredService<CursorWorkspaceViewModel>());
        }

        // The same must hold for the shipped shape of the graph without the Infrastructure project
        // repository: the composition helper leaves the resolver absent instead of resolving a factory
        // that cannot produce it, so every workspace panel still comes up (ТЗ §6.5).
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<ICliDetectionService>(FakeCliDetectionService.Degraded());
        services.AddAppUi();

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IProjectRepository>());
        Assert.NotNull(provider.GetRequiredService<WorkspaceViewModel>());
        Assert.NotNull(provider.GetRequiredService<CursorWorkspaceViewModel>());
        Assert.NotNull(provider.GetRequiredService<MirasimWorkspaceViewModel>());
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behavior of the Phase 11C frictionless onboarding: step navigation, workspace confirmation,
/// local CLI detection, built-in catalog discovery, persisted completion and the exclusively local
/// synthetic simulation. No API key, network request or paid model call is involved anywhere.
/// </summary>
public sealed class OnboardingViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompletingOnboarding_NotifiesTheBusyStateToBindings()
    {
        var viewModel = CreateViewModel();
        var observed = new System.Collections.Generic.List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsBusy)) observed.Add(viewModel.IsBusy);
        };
        await viewModel.CompleteAsync();
        Assert.Equal(new[] { true, false }, observed);
    }

    [Fact]
    public void StepRail_ExposesTheFourRequiredStepsInOrder()
    {
        var onboarding = CreateViewModel();

        Assert.Equal(4, onboarding.Steps.Count);
        Assert.Equal(
            new[]
            {
                OnboardingStepKind.WelcomeAndWorkspace,
                OnboardingStepKind.LocalCliDetection,
                OnboardingStepKind.WorkflowCatalogDiscovery,
                OnboardingStepKind.ReadySafeMode
            },
            onboarding.Steps.Select(step => step.Kind).ToArray());

        Assert.True(onboarding.IsFirstStep);
        Assert.True(onboarding.IsWelcomeStep);
        Assert.False(onboarding.IsLastStep);
    }

    [Fact]
    public void GoNextAndBack_WalkTheStepsAndGuardTheEnds()
    {
        var onboarding = CreateViewModel();

        onboarding.GoNext();
        Assert.True(onboarding.IsCliStep);
        Assert.False(onboarding.IsFirstStep);

        onboarding.GoBack();
        Assert.True(onboarding.IsFirstStep);

        onboarding.GoBack();
        Assert.True(onboarding.IsFirstStep, "The wizard never steps before the first page.");

        onboarding.GoToStep(OnboardingStepKind.WorkflowCatalogDiscovery);
        Assert.True(onboarding.IsCatalogStep);
        Assert.Equal(2, onboarding.CurrentStepIndex);

        onboarding.GoToStep(3);
        Assert.True(onboarding.IsLastStep);
        Assert.Equal("Завершить", onboarding.NextButtonText);
    }

    [Fact]
    public async Task InitializeAsync_LoadsTheBuiltInCatalogRoleMatrixAndDocumentTemplates()
    {
        var onboarding = CreateViewModel();

        await onboarding.InitializeAsync();

        Assert.True(onboarding.HasLoadedState);
        Assert.True(onboarding.HasCatalogTemplates);
        Assert.True(onboarding.HasRoleMatrix);
        Assert.True(onboarding.HasDocumentTemplates);
        Assert.Contains(onboarding.CatalogTemplates, template => template.IsBuiltIn);
        Assert.Contains(onboarding.RoleMatrix, role => role.RouteDisplay.Length > 0);
        Assert.Contains("шаблон", onboarding.CatalogSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmWorkspace_AcceptsAnExistingDirectoryAndPersistsIt()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var onboarding = CreateViewModel(settings: settings);
        var directory = CreateTempDirectory();

        try
        {
            await onboarding.ConfirmWorkspaceAsync(directory);

            Assert.True(onboarding.IsWorkspaceConfirmed);
            Assert.Equal(Path.GetFullPath(directory), onboarding.WorkspacePath);
            Assert.Equal(
                Path.GetFullPath(directory),
                settings.Values[OnboardingViewModel.WorkspaceSettingKey]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfirmWorkspace_RejectsRelativeAndMissingPathsWithoutBlockingTheWizard()
    {
        var onboarding = CreateViewModel();

        await onboarding.ConfirmWorkspaceAsync("relative\\path");
        Assert.False(onboarding.IsWorkspaceConfirmed);
        Assert.Contains("абсолютным", onboarding.WorkspaceStatus, StringComparison.OrdinalIgnoreCase);

        await onboarding.ConfirmWorkspaceAsync(
            Path.Combine(Path.GetTempPath(), "llmworkgui-missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(onboarding.IsWorkspaceConfirmed);
        Assert.Contains("не найден", onboarding.WorkspaceStatus, StringComparison.OrdinalIgnoreCase);

        // The wizard stays usable: the next step is still reachable.
        onboarding.GoNext();
        Assert.True(onboarding.IsCliStep);
    }

    [Fact]
    public async Task CompleteAsync_PersistsCompletionAndRaisesCloseRequested()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var onboarding = CreateViewModel(settings: settings);
        var closeRequests = 0;
        onboarding.CloseRequested += (_, _) => closeRequests++;

        await onboarding.CompleteAsync();

        Assert.True(onboarding.IsCompleted);
        Assert.Equal(1, closeRequests);
        Assert.Equal(bool.TrueString, settings.Values[OnboardingViewModel.CompletionSettingKey]);
        Assert.Contains("завершён", onboarding.CompletionDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dismiss_RaisesCloseRequestedWithoutMarkingCompletion()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var onboarding = CreateViewModel(settings: settings);
        var closeRequests = 0;
        onboarding.CloseRequested += (_, _) => closeRequests++;

        onboarding.DismissCommand.Execute(null);

        Assert.Equal(1, closeRequests);
        Assert.False(onboarding.IsCompleted);
        Assert.Empty(settings.Values);
    }

    [Fact]
    public async Task OpenWorkspaceAsync_MarksCompletionAndRaisesWorkspaceRequested()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var onboarding = CreateViewModel(settings: settings);
        var workspaceRequests = 0;
        onboarding.WorkspaceRequested += (_, _) => workspaceRequests++;

        await onboarding.OpenWorkspaceAsync();

        Assert.True(onboarding.IsCompleted);
        Assert.Equal(1, workspaceRequests);
        Assert.Equal(bool.TrueString, settings.Values[OnboardingViewModel.CompletionSettingKey]);
    }

    [Fact]
    public async Task Reload_WithTheSameSettings_RestoresTheCompletionState()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var first = CreateViewModel(settings: settings);
        await first.CompleteAsync();

        var second = CreateViewModel(settings: settings);
        await second.InitializeAsync();

        Assert.True(second.IsCompleted);
        Assert.Contains("завершён", second.CompletionDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_ConfirmsAPersistedWorkspaceDirectory()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var directory = CreateTempDirectory();

        try
        {
            await settings.SetValueAsync(OnboardingViewModel.WorkspaceSettingKey, directory);

            var onboarding = CreateViewModel(settings: settings);
            await onboarding.InitializeAsync();

            Assert.True(onboarding.IsWorkspaceConfirmed);
            Assert.Contains("подтверждён", onboarding.WorkspaceStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshCli_ForwardsTheObservedDetectionSnapshot()
    {
        var detection = FakeCliDetectionService.Degraded();
        var cliStatus = new CliStatusViewModel(detection, new FixedTimeProvider(Now));
        var onboarding = CreateViewModel(cliStatus: cliStatus);

        await onboarding.RefreshCliAsync();

        Assert.Equal(1, detection.DetectCallCount);
        Assert.Contains("Обнаружено CLI", onboarding.CliDetectionSummary, StringComparison.Ordinal);
        Assert.Equal(cliStatus.DetectionSummary, onboarding.CliDetectionSummary);
        Assert.True(onboarding.IsCliDegraded);
    }

    [Fact]
    public async Task InitializeAsync_ListsLocalBackendsWithHonestNotReportedStatus()
    {
        var onboarding = CreateViewModel();
        await onboarding.InitializeAsync();

        Assert.True(onboarding.HasBackends);
        Assert.Contains(
            onboarding.Backends,
            backend => backend.BackendDisplay.Contains("star-cliproxy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            onboarding.Backends,
            backend => backend.BackendDisplay.Contains("Mirasim", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            onboarding.Backends,
            backend => Assert.False(string.IsNullOrWhiteSpace(backend.StatusDisplay)));
    }

    [Fact]
    public async Task InitializeAsync_ReportsTheResolvedStarCliProxyGatewayAndConfiguredMirasimHost()
    {
        var onboarding = CreateViewModel(
            starCliProxyResolver: new StubStarCliProxyResolver(
                Application.StarCliProxy.StarCliProxyExecutableResolution.Found(
                    @"C:\tools\star-cliproxy.exe")),
            mirasimOptions: new Backends.Abstractions.Mirasim.MirasimOptions
            {
                Hostname = "127.0.0.1",
                Port = 4970
            });

        await onboarding.InitializeAsync();

        var gateway = onboarding.Backends.Single(backend =>
            backend.BackendDisplay.Contains("star-cliproxy", StringComparison.OrdinalIgnoreCase));
        Assert.True(gateway.IsDetected);
        Assert.Contains("star-cliproxy.exe", gateway.DetailDisplay, StringComparison.Ordinal);

        var mirasim = onboarding.Backends.Single(backend =>
            backend.BackendDisplay.Contains("Mirasim", StringComparison.OrdinalIgnoreCase));
        Assert.True(mirasim.IsDetected);
        Assert.Contains("127.0.0.1:4970", mirasim.DetailDisplay, StringComparison.Ordinal);
        Assert.Contains("не перезапускает", mirasim.DetailDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeSimulation_UsesOnlyTheLocalSyntheticFixture()
    {
        var onboarding = CreateViewModel();

        onboarding.StartSafeSimulationCommand.Execute(null);

        Assert.True(onboarding.IsSafeSimulationActive);
        Assert.True(onboarding.HasSimulationSteps);
        Assert.Equal(5, onboarding.SimulationSteps.Count);
        Assert.All(
            onboarding.SimulationSteps,
            step => Assert.Equal("SYNTHETIC", step.SyntheticLabel));
        Assert.Contains(
            onboarding.SimulationSteps,
            step => step.StageDisplay == "Coordinator");
        Assert.Contains("synthetic", onboarding.SafeSimulationStatusDisplay, StringComparison.OrdinalIgnoreCase);

        onboarding.StopSafeSimulationCommand.Execute(null);

        Assert.False(onboarding.IsSafeSimulationActive);
        Assert.Empty(onboarding.SimulationSteps);
    }

    [Fact]
    public async Task OpenProject_ReusesAnExistingProjectRowForTheSameDirectory()
    {
        var directory = CreateTempDirectory();
        var projects = new InMemoryProjectRepository();
        var onboarding = CreateViewModel(projectRepository: projects, settings: new InMemoryApplicationSettingsRepository());

        try
        {
            await onboarding.ConfirmWorkspaceAsync(directory);

            await onboarding.OpenProjectAsync();
            await onboarding.OpenProjectAsync();

            Assert.Equal(1, onboarding.ProjectOpenCount);
            var stored = await projects.ListAsync();
            Assert.Single(stored);
            Assert.Equal(Path.GetFullPath(directory), stored[0].RootPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedWizard_CanBeInvokedAgainWithoutBlocking()
    {
        var settings = new InMemoryApplicationSettingsRepository();
        var onboarding = CreateViewModel(settings: settings);
        await onboarding.CompleteAsync();

        onboarding.GoToStep(OnboardingStepKind.LocalCliDetection);
        Assert.True(onboarding.IsCliStep);

        onboarding.GoToStep(0);
        Assert.True(onboarding.IsWelcomeStep);
        Assert.True(onboarding.IsCompleted);
    }

    private static OnboardingViewModel CreateViewModel(
        CliStatusViewModel? cliStatus = null,
        WorkflowLibraryViewModel? workflowLibrary = null,
        InMemoryApplicationSettingsRepository? settings = null,
        InMemoryProjectRepository? projectRepository = null,
        Application.StarCliProxy.IStarCliProxyExecutableResolver? starCliProxyResolver = null,
        Backends.Abstractions.Mirasim.MirasimOptions? mirasimOptions = null) =>
        new(
            cliStatus,
            workflowLibrary ?? new WorkflowLibraryViewModel(),
            settings,
            projectRepository,
            new FixedTimeProvider(Now),
            starCliProxyResolver,
            mirasimOptions);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-onboarding-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubStarCliProxyResolver : Application.StarCliProxy.IStarCliProxyExecutableResolver
    {
        private readonly Application.StarCliProxy.StarCliProxyExecutableResolution _resolution;

        public StubStarCliProxyResolver(Application.StarCliProxy.StarCliProxyExecutableResolution resolution)
        {
            _resolution = resolution;
        }

        public Application.StarCliProxy.StarCliProxyExecutableResolution Resolve() => _resolution;
    }
}

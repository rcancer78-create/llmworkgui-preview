using System.IO;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.Application.Repositories;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class OnboardingPersistenceTests
{
    [Fact]
    public async Task EditingWorkspaceDuringSave_CannotConfirmAnotherPathOrCompleteTheWizard()
    {
        var settings = new PendingSettings();
        var model = new OnboardingViewModel(settings: settings);
        var saving = model.ConfirmWorkspaceAsync(Path.GetTempPath());
        Assert.True(model.IsBusy);
        Assert.False(model.IsWorkspaceConfirmed);
        model.WorkspacePath = Path.GetPathRoot(Path.GetTempPath())!;
        await model.CompleteAsync();
        Assert.False(model.IsCompleted);
        settings.Release.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.IsBusy);
        Assert.False(model.IsWorkspaceConfirmed);
        Assert.Equal(1, settings.Writes);
    }

    private sealed class PendingSettings : IApplicationSettingsRepository
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, string valueType = "String", CancellationToken cancellationToken = default)
        { Writes++; return Release.Task; }
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmWorkspace_SaveFailureDoesNotPublishConfirmation(bool cancel)
    {
        var model = new OnboardingViewModel(settings: new FailingSettings(cancel));
        try { await model.ConfirmWorkspaceAsync(Path.GetTempPath()); }
        catch (OperationCanceledException) when (cancel) { }
        Assert.False(model.IsWorkspaceConfirmed);
        Assert.DoesNotContain("подтверждён локально", model.WorkspaceStatus);
        Assert.DoesNotContain("secret-canary", model.StatusMessage + model.WorkspaceStatus);
    }

    [Fact]
    public async Task ConfirmWorkspace_WithoutStorageDoesNotPublishConfirmation()
    {
        var model = new OnboardingViewModel();
        await model.ConfirmWorkspaceAsync(Path.GetTempPath());
        Assert.False(model.IsWorkspaceConfirmed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveFailureOrCancellation_DoesNotCompleteOrClose(bool openWorkspace, bool cancel)
    {
        var settings = new FailingSettings(cancel);
        var model = new OnboardingViewModel(settings: settings);
        var closed = 0;
        model.CloseRequested += (_, _) => closed++;
        model.WorkspaceRequested += (_, _) => closed++;
        try
        {
            if (openWorkspace) await model.OpenWorkspaceAsync();
            else await model.CompleteAsync();
        }
        catch (OperationCanceledException) when (cancel) { }
        Assert.False(model.IsCompleted);
        Assert.False(model.IsBusy);
        Assert.Equal(0, closed);
        Assert.DoesNotContain("secret-canary", model.StatusMessage);
        var restarted = new OnboardingViewModel(settings: settings);
        await restarted.InitializeAsync();
        Assert.False(restarted.IsCompleted);
    }

    [Fact]
    public async Task MissingStorage_IsNotReportedAsDurableCompletion()
    {
        var model = new OnboardingViewModel();
        await model.CompleteAsync();
        Assert.False(model.IsCompleted);
    }

    private sealed class FailingSettings(bool cancel) : IApplicationSettingsRepository
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetValueAsync(string key, string value, string valueType = "String", CancellationToken cancellationToken = default) =>
            cancel ? Task.FromException(new OperationCanceledException()) : Task.FromException(new IOException("secret-canary"));
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}

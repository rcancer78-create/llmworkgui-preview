using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowAdaptationViewModelTests
{
    [Fact]
    public async Task ConfirmedActivationCannotBeRepeatedAfterParentReloadFails()
    {
        // Synthetic UI projection, not native identity proof. The service returns confirmed success.
        var harness = new AdaptationHarness();
        var vm = harness.CreateViewModel();
        await vm.OpenForVersionAsync(harness.Version, harness.Package, harness.Project);
        await vm.StartAdaptationAsync();
        var notifications = 0;
        vm.OnLibraryChangedAsync = () =>
        {
            notifications++;
            return Task.FromException(new IOException("synthetic parent refresh failure"));
        };

        await vm.AcceptAndActivateAsync();

        Assert.Single(harness.Activation.ActivationRequests);
        Assert.Single(harness.Adaptation.SavedSessions);
        Assert.Equal(1, notifications);
        Assert.False(vm.IsVisible);
        Assert.False(vm.IsSessionActive);
        Assert.Null(vm.ActiveSessionId);
        Assert.False(vm.CanAcceptAndActivate);
        Assert.False(vm.IsBusy);
        Assert.Contains("Активирован", vm.StatusMessage, StringComparison.Ordinal);

        await vm.AcceptAndActivateAsync();
        Assert.Single(harness.Activation.ActivationRequests);
        Assert.Single(harness.Adaptation.SavedSessions);
        Assert.Equal(1, notifications);
    }
}

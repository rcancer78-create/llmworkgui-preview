using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class NativeGatewayWorkspaceTests
{
    [Fact]
    public void HostStoppingCancelsTheActualComposerRequestAndWaitsForItsUncertainResult()
    {
        StaTestRunner.Run(() =>
        {
            using var lifetime = new ComposerStoppingLifetime();
            var turns = new FakeTurns();
            using var provider = CreateLifetimeProvider(lifetime, turns);
            var vm = provider.GetRequiredService<NativeGatewayWorkspaceViewModel>();
            Pump(vm.RefreshAsync()); vm.SelectedRoute = Route; vm.PromptInput = "Synthetic shutdown request";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<NativeGatewayTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken received = default;
            turns.Action = (_, token) => { received = token; entered.TrySetResult(); return completed.Task; };
            var operation = vm.SendAsync(); Pump(entered.Task);
            Task? drain = null;
            try
            {
                lifetime.StopApplication();
                Assert.True(received.IsCancellationRequested);
                Assert.True(vm.IsBusy);
                Assert.False(operation.IsCompleted);
                var hosted = provider.GetRequiredService<NativeGatewayComposerDrain>();
                Assert.Same(hosted, provider.GetServices<IHostedService>().OfType<NativeGatewayComposerDrain>().Single());
                drain = hosted.StopAsync(CancellationToken.None);
                Assert.False(drain.IsCompleted);
                Assert.False(vm.CanSend); Assert.False(vm.CanRefresh);
                Pump(vm.SendAsync()); Assert.Equal(1, turns.Calls);
            }
            finally
            {
                completed.TrySetResult(Success() with { State = ExecutionState.Ambiguous,
                    FailureReason = ExecutionFailureReason.UserCancelled, RequiresReconciliation = true });
                Pump(operation);
                if (drain is not null) Pump(drain);
            }
            Assert.False(vm.IsBusy); Assert.False(vm.CanSend); Assert.False(vm.CanRefresh);
            Assert.Empty(vm.Response); Assert.Contains("Блокировка проекта сохранена", vm.Outcome);
            Assert.Equal(1, turns.Calls);
        });
    }

    [Fact]
    public void ComposerDrainDeadlineDoesNotCompleteOrReleaseThePendingRequest()
    {
        StaTestRunner.Run(() =>
        {
            using var lifetime = new ComposerStoppingLifetime();
            var turns = new FakeTurns();
            using var provider = CreateLifetimeProvider(lifetime, turns);
            var vm = provider.GetRequiredService<NativeGatewayWorkspaceViewModel>();
            Pump(vm.RefreshAsync()); vm.SelectedRoute = Route; vm.PromptInput = "Synthetic drain deadline";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<NativeGatewayTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            turns.Action = (_, _) => { entered.TrySetResult(); return completed.Task; };
            var operation = vm.SendAsync(); Pump(entered.Task);
            try
            {
                using var deadline = new CancellationTokenSource();
                var drain = provider.GetRequiredService<NativeGatewayComposerDrain>().StopAsync(deadline.Token);
                Assert.False(drain.IsCompleted);
                deadline.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Pump(drain));
                Assert.True(vm.IsBusy); Assert.False(operation.IsCompleted);
                Assert.False(vm.CanSend); Assert.Equal(1, turns.Calls);
            }
            finally
            {
                completed.TrySetResult(Success() with { State = ExecutionState.Ambiguous,
                    FailureReason = ExecutionFailureReason.UserCancelled, RequiresReconciliation = true });
                Pump(operation); Pump(vm.WaitForIdleAsync());
            }
            Assert.Contains("Блокировка проекта сохранена", vm.Outcome);
            Assert.Equal(1, turns.Calls);
        });
    }

    [Fact]
    public void StoppedHostRefusesDirectSendAndCommandEvenWithPreviouslyReadyRoute()
    {
        StaTestRunner.Run(() =>
        {
            using var lifetime = new ComposerStoppingLifetime();
            var turns = new FakeTurns();
            using var provider = CreateLifetimeProvider(lifetime, turns);
            var vm = provider.GetRequiredService<NativeGatewayWorkspaceViewModel>();
            Pump(vm.RefreshAsync()); vm.SelectedRoute = Route; vm.PromptInput = "Synthetic stopped request";
            Assert.True(vm.CanSend);
            lifetime.StopApplication();
            Assert.False(vm.CanSend); Assert.False(vm.SendCommand.CanExecute(null));
            Assert.False(vm.CanRefresh); Assert.False(vm.RefreshCommand.CanExecute(null));
            Pump(vm.SendAsync()); vm.SendCommand.Execute(null);
            Assert.Equal(0, turns.Calls); Assert.False(vm.IsBusy);
        });
    }

    private static ServiceProvider CreateLifetimeProvider(ComposerStoppingLifetime lifetime, FakeTurns turns)
    {
        var services = new ServiceCollection();
        services.AddApplication(); services.AddAppUi();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<IProjectRepository>(new FakeProjects());
        services.AddSingleton<IApplicationInstanceGuard>(new Guard());
        services.AddSingleton<INativeGatewayRouteCatalog>(new FakeCatalog());
        services.AddSingleton<INativeGatewayTurnService>(turns);
        return services.BuildServiceProvider();
    }

    private sealed class ComposerStoppingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}

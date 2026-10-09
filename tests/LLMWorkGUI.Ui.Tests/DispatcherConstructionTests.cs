using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Cursor dispatcher isolation")]
public sealed class DispatcherConstructionTests
{
    [Fact]
    public async Task ActivityScheduler_FirstResolvedOnWorker_PostsToApplicationDispatcher()
    {
        StaTestRunner.EnsureApplication();
        var uiThread = StaTestRunner.Run(() => Environment.CurrentManagedThreadId);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddUnifiedWorkspaceShell();
        using var provider = services.BuildServiceProvider();
        var scheduler = await Task.Run(() => provider.GetRequiredService<IActivityUiScheduler>());
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Task.Run(() => scheduler.Post(() => published.TrySetResult(Environment.CurrentManagedThreadId)));

        Assert.Equal(uiThread, await published.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CursorConstructedOnWorker_PublishesStreamCollectionOnApplicationDispatcher()
    {
        StaTestRunner.EnsureApplication();
        var uiThread = StaTestRunner.Run(() => Environment.CurrentManagedThreadId);
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await Task.Run(() => new CursorWorkspaceViewModel(lifecycle));
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StreamEvents.CollectionChanged += (_, _) => published.TrySetResult(Environment.CurrentManagedThreadId);

        await Task.Run(() => lifecycle.RaiseStreamEvent(new CursorAcpStreamEvent.StatusUpdate
        { Method = "synthetic/status", Phase = "generating" }));

        Assert.Equal(uiThread, await published.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeServerConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultComposition_LazilySharesAssignedEndpointBetweenHttpAndEvents_AndStopsOnDispose(bool synchronousDisposal)
    {
        var manager = new Manager();
        var requests = new ConcurrentQueue<Uri>();
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/event"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n", Encoding.UTF8, "text/event-stream") }
                : StubHttpMessageHandler.Json("{}"));
        }));
        var services = new ServiceCollection();
        services.AddSingleton<IOpenCodeServerManager>(manager);
        services.AddSingleton(http);
        services.AddOpenCodeBackend();
        var provider = services.BuildServiceProvider();
        try
        {
            var client = provider.GetRequiredService<IOpenCodeClient>();
            var stream = provider.GetRequiredService<IOpenCodeEventStreamService>();
            Assert.Equal(0, manager.Starts);
            await using var events = stream.SubscribeAsync().GetAsyncEnumerator();
            await Task.WhenAll(client.PingAsync(), events.MoveNextAsync().AsTask());
            Assert.Equal("server.connected", events.Current.Type);
            Assert.Equal(1, manager.Starts);
            Assert.Equal(manager.Instance.BaseUrl, client.BaseUrl);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, uri => Assert.Equal(manager.Instance.BaseUrl.Authority, uri.Authority));
        }
        finally
        {
            if (synchronousDisposal) provider.Dispose(); else await provider.DisposeAsync();
        }
        Assert.Equal(1, manager.Stops);
        Assert.False(manager.Instance.IsAlive);
    }

    [Fact]
    public async Task ExplicitPortComposition_UsesExternalEndpointWithoutStartingOrStoppingServer()
    {
        var manager = new Manager();
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(61234, request.RequestUri!.Port);
            return Task.FromResult(StubHttpMessageHandler.Json("{}"));
        }));
        var services = new ServiceCollection();
        services.AddSingleton<IOpenCodeServerManager>(manager);
        services.AddSingleton(http);
        services.AddOpenCodeBackend();
        services.Configure<OpenCodeServerOptions>(options => options.Port = 61234);
        await using (var provider = services.BuildServiceProvider())
            Assert.True(await provider.GetRequiredService<IOpenCodeClient>().PingAsync());
        Assert.Equal(0, manager.Starts);
        Assert.Equal(0, manager.Stops);
    }

    [Fact]
    public async Task FailedStartup_CanBeRetriedBeforeAnyRequestWasDispatched()
    {
        var manager = new Manager();
        manager.Start = _ => throw new OpenCodeServerStartupException("startup failed");
        await using var connection = new OpenCodeServerConnection(manager);
        await Assert.ThrowsAsync<OpenCodeServerStartupException>(() => connection.GetBaseUrlAsync());
        Assert.Null(connection.Current);
        manager.Start = _ => Task.FromResult<IOpenCodeServerInstance>(manager.Instance);
        Assert.Equal(manager.Instance.BaseUrl, await connection.GetBaseUrlAsync());
        Assert.Equal(2, manager.Starts);
    }

    [Fact]
    public async Task DeadOwnedInstance_IsNotSilentlyReplaced()
    {
        var manager = new Manager();
        await using var connection = new OpenCodeServerConnection(manager);
        await connection.GetBaseUrlAsync();
        manager.Instance.IsAlive = false;
        await Assert.ThrowsAsync<OpenCodeClientException>(() => connection.GetBaseUrlAsync());
        Assert.Equal(1, manager.Starts);
    }

    [Fact]
    public async Task CancelledWaitingCaller_DoesNotCancelAnotherCallersStartup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager();
        manager.Start = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return manager.Instance; };
        await using var connection = new OpenCodeServerConnection(manager);
        var first = connection.GetBaseUrlAsync();
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var second = connection.GetBaseUrlAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        Assert.Equal(manager.Instance.BaseUrl, await first);
        Assert.Equal(1, manager.Starts);
    }

    [Fact]
    public async Task CancelledStartup_LeavesNoCachedInstance_AndNextCallerCanStart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager();
        manager.Start = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return manager.Instance; };
        await using var connection = new OpenCodeServerConnection(manager);
        using var cancellation = new CancellationTokenSource();
        var first = connection.GetBaseUrlAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(connection.Current);
        manager.Start = _ => Task.FromResult<IOpenCodeServerInstance>(manager.Instance);
        await connection.GetBaseUrlAsync();
        Assert.Equal(2, manager.Starts);
    }

    [Fact]
    public async Task DisposalDuringStartup_StopsTheInstance_AndRejectsLaterRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager();
        manager.Start = async token => { entered.SetResult(); await release.Task.WaitAsync(token); return manager.Instance; };
        var connection = new OpenCodeServerConnection(manager);
        var startup = connection.GetBaseUrlAsync();
        await entered.Task;
        var disposal = connection.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => startup);
        await disposal;
        await connection.DisposeAsync();
        Assert.Equal(1, manager.Stops);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.GetBaseUrlAsync());
    }

    [Fact]
    public async Task DisposalTimeout_RetainsLateStartupAndCleansItWithoutPublishingEndpoint()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IOpenCodeServerInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Manager { Start = _ => { entered.TrySetResult(); return release.Task; } };
        var connection = new OpenCodeServerConnection(manager, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var startup = connection.GetBaseUrlAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => connection.DisposeAsync().AsTask());
            Assert.Null(connection.Current);
        }
        finally { release.TrySetResult(manager.Instance); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => startup);
        await connection.DisposeAsync();
        Assert.Equal(1, manager.Stops);
        Assert.False(manager.Instance.IsAlive);
    }

    [Fact]
    public async Task ViewOnlyInstance_CannotStartNativeServer()
    {
        var manager = new Manager();
        using var guard = new ViewOnlyGuard();
        await using var connection = new OpenCodeServerConnection(manager, guard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetBaseUrlAsync());
        Assert.Equal(0, manager.Starts);
    }

    private sealed class Manager : IOpenCodeServerManager
    {
        public Instance Instance { get; } = new();
        public int Starts;
        public int Stops;
        public Func<CancellationToken, Task<IOpenCodeServerInstance>> Start { get; set; }
        public Manager() => Start = _ => Task.FromResult<IOpenCodeServerInstance>(Instance);
        public Task<IOpenCodeServerInstance> StartServerAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Starts); return Start(cancellationToken); }
        public Task StopServerAsync(IOpenCodeServerInstance instance, CancellationToken cancellationToken = default)
        { Assert.Same(Instance, instance); Interlocked.Increment(ref Stops); Instance.IsAlive = false; return Task.CompletedTask; }
    }

    private sealed class Instance : IOpenCodeServerInstance
    {
        public string InstanceId => "owned-test";
        public int? ProcessId => null;
        public int AssignedPort => 54321;
        public Uri BaseUrl { get; } = new("http://127.0.0.1:54321");
        public bool IsAlive { get; set; } = true;
        public DateTimeOffset StartedAtUtc => DateTimeOffset.UtcNow;
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View only");
        public void Dispose() { }
    }
}

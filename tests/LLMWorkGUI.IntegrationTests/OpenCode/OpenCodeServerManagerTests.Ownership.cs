using System.Net;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;
using Microsoft.Extensions.Options;
using Xunit;
using System.Diagnostics;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed partial class OpenCodeServerManagerTests
{
    // Scripted supervisor contract results; these tests do not claim native OpenCode activation.
    private sealed class PendingCleanupSupervisor : IProcessSupervisor
    {
        public ProcessTerminationReason Reason=ProcessTerminationReason.CleanupPending;
        public bool ReportPort=true;
        public int Executions;
        public int CleanupWaits;
        public string? Failure;
        public bool UnsupportedCleanup;
        public bool FaultCleanup;
        public bool SynchronousFailure;
        public string? ResultExecutionOverride;
        public readonly TaskCompletionSource Cleanup=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ResultReturned=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? WaitedExecution;
        public Task WaitForStartupCleanupAsync(string executionId,CancellationToken token=default)
        {
            WaitedExecution=executionId; Interlocked.Increment(ref CleanupWaits);
            if (UnsupportedCleanup) throw new NotSupportedException("owned private cleanup value");
            if (FaultCleanup) throw new IOException("owned private cleanup value");
            return Cleanup.Task.WaitAsync(token);
        }
        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? progress=null,CancellationToken token=default)
        {
            Interlocked.Increment(ref Executions);
            if (SynchronousFailure) throw new IOException("owned private supervisor value");
            if(ReportPort) progress?.Report(new(ProcessStreamKind.StdOut,"Listening on http://127.0.0.1:54321",DateTimeOffset.UtcNow,43));
            var completion=new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(()=>
            {
                if(Failure=="fault") { completion.TrySetException(new IOException("owned private supervisor value")); ResultReturned.TrySetResult(); return; }
                if(Failure=="cancel") { completion.TrySetCanceled(); ResultReturned.TrySetResult(); return; }
                var now=DateTimeOffset.UtcNow;
                completion.TrySetResult(new()
                {
                    ExecutionId=ResultExecutionOverride ?? specification.ExecutionId,ProcessId=123,TerminationReason=Reason,StartedAtUtc=now,ExitedAtUtc=now,
                    RunDirectory="",StandardOutputLogPath="",StandardErrorLogPath="",StandardOutputBytes=0,StandardErrorBytes=0,
                    StandardOutputHead="",StandardOutputTail="",StandardErrorHead="",StandardErrorTail="",OutputOverflowed=false
                });
                ResultReturned.TrySetResult();
            });
            return completion.Task;
        }
    }
    private static OpenCodeServerManager CleanupManager(PendingCleanupSupervisor supervisor,HttpClient http)
        =>new(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath=typeof(OpenCodeServerManagerTests).Assembly.Location,
            StartupTimeout=TimeSpan.FromMilliseconds(100),DisposeTimeout=TimeSpan.FromSeconds(2)
        }),supervisor,new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")),http,new FixedProcessIdResolver(123));

    [Theory]
    [InlineData(ProcessTerminationReason.StartupPending)]
    [InlineData(ProcessTerminationReason.CleanupPending)]
    public async Task StopCannotConfirmTerminationUntilExactRetainedSupervisorCleanupCompletes(ProcessTerminationReason reason)
    {
        var supervisor=new PendingCleanupSupervisor { Reason=reason }; using var http=CreateHttpClient();
        var manager=CleanupManager(supervisor,http); var instance=await manager.StartServerAsync();
        var stop=manager.StopServerAsync(instance);
        try
        {
            await supervisor.ResultReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<TimeoutException>(()=>stop.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(instance.InstanceId,supervisor.WaitedExecution);
        }
        finally { supervisor.Cleanup.TrySetResult(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1,supervisor.CleanupWaits);
    }

    [Fact]
    public async Task UnconfirmedStartupCleanupBlocksReplacementBeforeAnotherNativeExecute()
    {
        var supervisor=new PendingCleanupSupervisor { ReportPort=false }; using var http=CreateHttpClient();
        var manager=CleanupManager(supervisor,http); var first=manager.StartServerAsync();
        try
        {
            await supervisor.ResultReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            supervisor.ReportPort=true;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>manager.StartServerAsync());
            Assert.Equal(1,supervisor.Executions);
        }
        finally { supervisor.Cleanup.TrySetResult(); }
        await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>first);
        var replacement=await manager.StartServerAsync(); await manager.StopServerAsync(replacement);
        Assert.Equal(2,supervisor.Executions);
    }

    [Fact]
    public async Task ForeignManagerCannotAcknowledgeAnotherManagersLiveInstance()
    {
        var supervisor=new PendingCleanupSupervisor { Reason=ProcessTerminationReason.UserCancelled }; using var http=CreateHttpClient();
        var owner=CleanupManager(supervisor,http); var foreign=CleanupManager(supervisor,http); var instance=await owner.StartServerAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(()=>foreign.StopServerAsync(instance));
            Assert.True(instance.IsAlive); Assert.False(instance.IsTerminationConfirmed);
        }
        finally { await owner.StopServerAsync(instance); }
        Assert.True(instance.IsTerminationConfirmed);
    }

    private sealed class FailingProcessIdResolver : IProcessIdResolver
    {
        public int? ResolveChildProcessId(string path,DateTimeOffset startedAfter)
            => throw new InvalidOperationException("owned private resolver value");
    }
    [Fact]
    public async Task NativePortBeforeResolverFailureStillRetainsStartupCleanupBeforeAnotherStart()
    {
        var supervisor=new PendingCleanupSupervisor(); using var http=CreateHttpClient();
        var manager=new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath=typeof(OpenCodeServerManagerTests).Assembly.Location,
            DisposeTimeout=TimeSpan.FromSeconds(2)
        }),supervisor,new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")),http,new FailingProcessIdResolver());
        var first=manager.StartServerAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(()=>first.WaitAsync(TimeSpan.FromMilliseconds(100)));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>manager.StartServerAsync());
            Assert.Equal(1,supervisor.Executions);
        }
        finally { supervisor.Cleanup.TrySetResult(); }
        var error=await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>first);
        Assert.DoesNotContain("owned private resolver value",error.ToString());
        Assert.Equal(1,supervisor.CleanupWaits); Assert.Equal(0,manager.PendingStartupCleanupCount);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    [InlineData("wrong-id")]
    [InlineData("unknown-reason")]
    [InlineData("unsupported-cleanup")]
    [InlineData("fault-cleanup")]
    public async Task StopWithoutValidExactTerminationKeepsOwnerAndCannotAcknowledgeOrReplaceIt(string mode)
    {
        var supervisor=new PendingCleanupSupervisor
        {
            Failure=mode is "fault" or "cancel" ? mode : null,
            ResultExecutionOverride=mode=="wrong-id" ? "foreign-execution" : null,
            Reason=mode=="unknown-reason" ? (ProcessTerminationReason)999 : ProcessTerminationReason.CleanupPending,
            UnsupportedCleanup=mode=="unsupported-cleanup",FaultCleanup=mode=="fault-cleanup"
        };
        using var http=CreateHttpClient(); var manager=CleanupManager(supervisor,http); var instance=await manager.StartServerAsync();
        var error=await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>manager.StopServerAsync(instance));
        Assert.DoesNotContain("owned private",error.ToString()); Assert.False(instance.IsTerminationConfirmed);
        Assert.Equal(1,manager.OwnedInstanceCount);
        await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>manager.StopServerAsync(instance));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>manager.StartServerAsync());
        Assert.Equal(1,supervisor.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousOrAsynchronousStartupFaultRetainsUnknownOwnerWithoutRawErrorOrReplacement(bool synchronous)
    {
        var supervisor=new PendingCleanupSupervisor { SynchronousFailure=synchronous,ReportPort=false,Failure="fault" };
        using var http=CreateHttpClient(); var manager=CleanupManager(supervisor,http);
        var error=await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>manager.StartServerAsync());
        Assert.DoesNotContain("owned private",error.ToString()); Assert.Equal(1,manager.PendingStartupCleanupCount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>manager.StartServerAsync()); Assert.Equal(1,supervisor.Executions);
    }

    [Fact]
    public async Task BoundedStopTimeoutKeepsOneSharedCleanupAndOnlyThatConfirmationAllowsReplacement()
    {
        var supervisor=new PendingCleanupSupervisor(); var requests=0;
        using var http=new HttpClient(new StubHttpMessageHandler((_,_)=> { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var manager=new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath=typeof(OpenCodeServerManagerTests).Assembly.Location,DisposeTimeout=TimeSpan.FromMilliseconds(50) }),
            supervisor,new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")),http,new FixedProcessIdResolver(123));
        var instance=await manager.StartServerAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(()=>manager.StopServerAsync(instance));
            await Assert.ThrowsAsync<TimeoutException>(()=>manager.StopServerAsync(instance));
            Assert.False(instance.IsAlive); Assert.False(instance.IsTerminationConfirmed); Assert.Equal(1,manager.OwnedInstanceCount);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>manager.StartServerAsync());
            Assert.Equal(1,supervisor.Executions); Assert.Equal(1,requests); Assert.Equal(1,supervisor.CleanupWaits);
        }
        finally { supervisor.Cleanup.TrySetResult(); }
        await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(instance.IsTerminationConfirmed); Assert.Equal(0,manager.OwnedInstanceCount);
        var next=await manager.StartServerAsync(); await manager.StopServerAsync(next);
        Assert.Equal(2,supervisor.Executions); Assert.Equal(2,requests);
    }

    [Fact]
    public async Task ConcurrentStopsWaitForOneExactCleanupAndOneDisposeRequest()
    {
        var supervisor=new PendingCleanupSupervisor(); var requests=0;
        using var http=new HttpClient(new StubHttpMessageHandler((_,_)=> { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        var manager=CleanupManager(supervisor,http); var instance=await manager.StartServerAsync();
        var stops=Enumerable.Range(0,8).Select(_=>manager.StopServerAsync(instance)).ToArray();
        try
        {
            await supervisor.ResultReturned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(stops,task=>Assert.False(task.IsCompleted)); Assert.False(instance.IsTerminationConfirmed);
        }
        finally { supervisor.Cleanup.TrySetResult(); }
        await Task.WhenAll(stops); Assert.Equal(1,requests); Assert.Equal(1,supervisor.CleanupWaits);
        await manager.StopServerAsync(instance); Assert.True(instance.IsTerminationConfirmed);
    }

    [Fact]
    public async Task DisposeAcknowledgementExceptionCannotReplaceConfirmedSupervisorTermination()
    {
        var supervisor=new PendingCleanupSupervisor { Reason=ProcessTerminationReason.UserCancelled };
        using var http=new HttpClient(new StubHttpMessageHandler((_,_)=>throw new InvalidOperationException("owned private dispose value")));
        var manager=CleanupManager(supervisor,http); var instance=await manager.StartServerAsync();
        await manager.StopServerAsync(instance); Assert.True(instance.IsTerminationConfirmed); Assert.Equal(0,manager.OwnedInstanceCount);
    }

    [Fact]
    public async Task AlreadyCancelledStartupDoesNotClaimUnknownOwnershipOrBeginExecution()
    {
        var supervisor=new PendingCleanupSupervisor { Reason=ProcessTerminationReason.UserCancelled }; using var http=CreateHttpClient();
        var manager=CleanupManager(supervisor,http); using var cts=new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.StartServerAsync(cts.Token));
        Assert.Equal(0,manager.PendingStartupCleanupCount); Assert.Equal(0,supervisor.Executions);
        var instance=await manager.StartServerAsync(); await manager.StopServerAsync(instance);
    }

    private sealed class ObservedRealSupervisor(ProcessSupervisor inner,Action<ProcessStartSpecification>? before=null) : IProcessSupervisor
    {
        public int Executions;
        public int CleanupWaits;
        public string? ExecutionId;
        public Task<ProcessExecutionResult>? ExecutionTask;
        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,IProgress<ProcessOutputEvent>? progress=null,CancellationToken token=default)
        { Interlocked.Increment(ref Executions); ExecutionId=specification.ExecutionId; before?.Invoke(specification); return ExecutionTask=inner.ExecuteAsync(specification,progress,token); }
        public Task WaitForStartupCleanupAsync(string executionId,CancellationToken token=default)
        { Assert.Equal(ExecutionId,executionId); Interlocked.Increment(ref CleanupWaits); return inner.WaitForStartupCleanupAsync(executionId,token); }
    }

    [Fact]
    public async Task ConnectionDisposalAfterFailedStartupCannotAcknowledgePendingCleanup()
    {
        var supervisor = new PendingCleanupSupervisor { ReportPort = false };
        using var http = CreateHttpClient();
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath = typeof(OpenCodeServerManagerTests).Assembly.Location,
            StartupTimeout = TimeSpan.FromMilliseconds(50), DisposeTimeout = TimeSpan.FromMilliseconds(50)
        }), supervisor, new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")), http,
            new FixedProcessIdResolver(null));
        var connection = new OpenCodeServerConnection(manager, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => connection.GetBaseUrlAsync());
            Assert.Equal(1, manager.PendingStartupCleanupCount);
            Assert.Null(connection.Current);
            await Assert.ThrowsAsync<TimeoutException>(() => connection.DisposeAsync().AsTask());
            Assert.Equal(1, manager.PendingStartupCleanupCount);
            Assert.Equal(1, supervisor.Executions);
            Assert.Equal(1, supervisor.CleanupWaits);
        }
        finally
        {
            supervisor.Cleanup.TrySetResult();
            await connection.DisposeAsync();
        }
        Assert.Equal(0, manager.PendingStartupCleanupCount);
        Assert.Equal(1, supervisor.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionDisposalAfterFailedStartupCannotAcknowledgeUnknownOwner(bool synchronous)
    {
        var supervisor = new PendingCleanupSupervisor
        { ReportPort = false, Failure = "fault", SynchronousFailure = synchronous };
        using var http = CreateHttpClient();
        var manager = CleanupManager(supervisor, http);
        var connection = new OpenCodeServerConnection(manager);
        await Assert.ThrowsAsync<OpenCodeServerStartupException>(() => connection.GetBaseUrlAsync());
        Assert.Null(connection.Current);
        var error = await Assert.ThrowsAsync<OpenCodeServerStartupException>(() => connection.DisposeAsync().AsTask());
        Assert.DoesNotContain("owned private", error.ToString());
        await Assert.ThrowsAsync<OpenCodeServerStartupException>(() => connection.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.GetBaseUrlAsync());
        Assert.Equal(1, manager.PendingStartupCleanupCount);
        Assert.Equal(1, supervisor.Executions);
    }

    [Fact]
    public async Task RealSupervisorLateOsStartupRemainsOwnedUntilPhysicalCleanupAndThenAllowsNewServer()
    {
        using var fake=new OpenCodeFakeServer("Listening on http://127.0.0.1:54321"); using var data=new TestDirectory();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim(); Process? owned=null; var starts=0;
        var real=new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions { StartupTimeout=TimeSpan.FromMilliseconds(50),GracefulShutdownTimeout=TimeSpan.FromMilliseconds(100) }),
            new StorageOptions { AppDataDirectory=data.Root },null,null,process=>
            {
                if(Interlocked.Increment(ref starts)==1) { entered.TrySetResult(); release.Wait(); }
                var ok=process.Start(); if(starts==1) { owned=Process.GetProcessById(process.Id); _=owned.SafeHandle; } return ok;
            });
        var supervisor=new ObservedRealSupervisor(real); using var http=CreateHttpClient();
        var manager=new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath=fake.ScriptPath,StartupTimeout=TimeSpan.FromSeconds(5),DisposeTimeout=TimeSpan.FromMilliseconds(100) }),
            supervisor,new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")),http,new FixedProcessIdResolver(null));
        var first=manager.StartServerAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Assert.ThrowsAsync<TimeoutException>(()=>first);
            Assert.Equal(1,manager.PendingStartupCleanupCount); Assert.Equal(1,supervisor.CleanupWaits);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>manager.StartServerAsync()); Assert.Equal(1,starts);
        }
        finally { release.Set(); }
        try
        {
            await real.WaitForStartupCleanupAsync(supervisor.ExecutionId!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(owned); Assert.True(owned.HasExited);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while(manager.PendingStartupCleanupCount!=0) await Task.Delay(10,timeout.Token);
            var next=await manager.StartServerAsync();
            try { await manager.StopServerAsync(next); }
            catch(TimeoutException)
            {
                // The deliberately short caller budget does not imply physical OS cleanup is done.
                Assert.False(next.IsTerminationConfirmed); Assert.Equal(1,manager.OwnedInstanceCount);
                await Assert.IsType<OpenCodeServerInstance>(next).ExecutionTask.WaitAsync(TimeSpan.FromSeconds(20));
                await manager.StopServerAsync(next);
            }
            Assert.True(next.IsTerminationConfirmed); Assert.Equal(2,starts);
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task RealSupervisorPostStartSpoolFailureCleanupIsObservedBeforeStartupOwnerIsReleased()
    {
        using var fake=new OpenCodeFakeServer("Listening on http://127.0.0.1:54321"); using var data=new TestDirectory(); Process? owned=null;
        var real=new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions { GracefulShutdownTimeout=TimeSpan.FromMilliseconds(100) }),new StorageOptions { AppDataDirectory=data.Root },null,null,
            process=> { var ok=process.Start(); owned=Process.GetProcessById(process.Id); _=owned.SafeHandle; return ok; });
        var supervisor=new ObservedRealSupervisor(real,spec=>Directory.CreateDirectory(Path.Combine(AppDataPaths.GetRunDirectory(data.Root,spec.ExecutionId),ProcessSupervisorOptions.StandardOutputFileName)));
        using var http=CreateHttpClient(); var manager=new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        { CustomExecutablePath=fake.ScriptPath,StartupTimeout=TimeSpan.FromSeconds(5),DisposeTimeout=TimeSpan.FromSeconds(5) }),supervisor,
            new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")),http,new FixedProcessIdResolver(null));
        try
        {
            await Assert.ThrowsAsync<OpenCodeServerStartupException>(()=>manager.StartServerAsync());
            Assert.NotNull(owned); Assert.True(owned.HasExited);
            Assert.True(supervisor.ExecutionTask!.IsCompletedSuccessfully);
            var completed = await supervisor.ExecutionTask!;
            Assert.NotNull(completed.ExitCode);
            Assert.True(completed.OutputOverflowed);
            Assert.DoesNotContain(completed.TerminationReason, new[] { ProcessTerminationReason.StartupPending, ProcessTerminationReason.CleanupPending });
            // The actual joined result is no longer a pending cleanup; the optional late-start
            // barrier is reserved for pending results, not required after physical completion.
            Assert.Equal(0,supervisor.CleanupWaits);
            Assert.Equal(0,manager.PendingStartupCleanupCount); Assert.Equal(0,manager.OwnedInstanceCount);
        }
        finally { owned?.Dispose(); }
    }
}

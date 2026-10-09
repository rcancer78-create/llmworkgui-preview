using System.Diagnostics;
using System.Text;
using LLMWorkGUI.Infrastructure.Concurrency;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class NamedMutexOwnershipTests
{
    [Fact]
    public void SameCallingThread_CannotAcquireSecondSupervisor()
    {
        using var directory = new TestDirectory();
        using var first = new ApplicationInstanceGuard(directory.Root);
        using var second = new ApplicationInstanceGuard(directory.Root);
        Assert.True(first.IsPrimarySupervisor);
        Assert.True(second.IsViewOnly);
    }

    [Fact]
    public void CrossThreadDispose_WithKeeperHandle_ReleasesMutexBeforeReturning()
    {
        var name = @"Local\LLMWorkGUI_Test_" + Guid.NewGuid().ToString("N");
        using var keeper = new Mutex(false, name);
        using var scope = NamedMutexScope.TryAcquire(name);
        Assert.NotNull(scope);
        bool acquired = false;
        Exception? failure = null;
        var disposer = new Thread(() =>
        {
            try
            {
                scope.Dispose();
                using var contender = new Mutex(false, name);
                acquired = contender.WaitOne(0);
                if (acquired) contender.ReleaseMutex();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        disposer.Start();
        Assert.True(disposer.Join(TimeSpan.FromSeconds(5)));
        // Clean up the old implementation's leaked ownership on this original thread.
        if (!acquired) keeper.ReleaseMutex();
        Assert.Null(failure);
        Assert.True(acquired);
    }

    [Fact]
    public async Task ConcurrentFirstAcquisition_GrantsExactlyOneOwner()
    {
        var name = @"Local\LLMWorkGUI_Test_" + Guid.NewGuid().ToString("N");
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            return NamedMutexScope.TryAcquire(name);
        })).ToArray();
        start.Set();
        var scopes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Single(scopes.Where(scope => scope is not null)); }
        finally { foreach (var scope in scopes) scope?.Dispose(); }
    }

    [Fact]
    public async Task SeparateProcess_SeesExclusionAndRelease_WithExistingNameContract()
    {
        using var directory = new TestDirectory();
        var name = NamedMutexNames.ForSupervisor(directory.Root);
        using var keeper = new Mutex(false, name);
        using var guard = new ApplicationInstanceGuard(directory.Root);
        Assert.True(guard.IsPrimarySupervisor);
        Assert.Equal(9, await ProbeFromSeparateProcess(name));
        await Task.Run(guard.Dispose);
        Assert.Equal(0, await ProbeFromSeparateProcess(name));
    }

    [Fact]
    public async Task ConcurrentDispose_ReleasesExactlyOnce()
    {
        var name = @"Local\LLMWorkGUI_Test_" + Guid.NewGuid().ToString("N");
        using var scope = NamedMutexScope.TryAcquire(name);
        Assert.NotNull(scope);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(scope.Dispose)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        using var next = NamedMutexScope.TryAcquire(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void InvalidName_PropagatesAcquisitionFailure()
    {
        Assert.ThrowsAny<Exception>(() => NamedMutexScope.TryAcquire("invalid\\nested\\mutex"));
    }

    private static async Task<int> ProbeFromSeparateProcess(string name)
    {
        const string script = """
            $mutex = [Threading.Mutex]::new($false, $env:LLMWORK_TEST_MUTEX)
            try {
                $held = $mutex.WaitOne(0)
                if ($held) { $mutex.ReleaseMutex(); exit 0 }
                exit 9
            } finally { $mutex.Dispose() }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["LLMWORK_TEST_MUTEX"] = name;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("", await stderr);
            await stdout;
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}

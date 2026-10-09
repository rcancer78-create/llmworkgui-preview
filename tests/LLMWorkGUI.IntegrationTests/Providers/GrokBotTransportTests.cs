using LLMGateway.Native;
using LLMWorkGUI.Infrastructure.GrokBot;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

/// <summary>Real Node subprocesses, synthetic local scripts; no session or network access.</summary>
public sealed class GrokBotTransportTests
{
    [Fact]
    public async Task ProductionTransportRejectsBeforeStartingAnyExecutable()
    {
        var transport = new GrokBotReviewTransport();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.CheckSessionAsync("must-not-be-launched.exe", CancellationToken.None));
        Assert.Contains("отключена", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ReviewAsync("must-not-be-launched.exe", "review", CancellationToken.None));
    }

    [Fact]
    public async Task BundledSessionRefusesDesktopCredentialAccess()
    {
        using var fixture = new Fixture("");
        var module = Path.Combine(AppContext.BaseDirectory, "GrokBot", "session.mjs");
        var script = "const modulePath=" + System.Text.Json.JsonSerializer.Serialize(module) + ";\n" + """
            import fs from 'node:fs';
            import assert from 'node:assert/strict';
            const original = fs.readFileSync(modulePath, 'utf8');
            const session = await import('data:text/javascript;base64,' + Buffer.from(original).toString('base64'));
            assert.throws(() => session.loadSession(), /integration is disabled/);
            assert.doesNotMatch(original, /node:fs|node:child_process|node:crypto|process\.env|sand-secrets|Unprotect/);
            console.log('session-disabled-ok');
            """;
        var file = Path.Combine(fixture.Root.FullName, "disabled-session-fixture.mjs");
        await File.WriteAllTextAsync(file, script);
        var start = new System.Diagnostics.ProcessStartInfo(fixture.Node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = fixture.Root.FullName
        };
        start.ArgumentList.Add(file);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("", await stderr);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("session-disabled-ok", await stdout);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task BundledFrameDecoderHandlesFragmentationCompactionAndInvalidFrames()
    {
        using var fixture = new Fixture("");
        var module = new Uri(Path.Combine(AppContext.BaseDirectory, "GrokBot", "transport.mjs")).AbsoluteUri;
        var script = "import {FrameBuffer,decodeFrames,envelope} from " + System.Text.Json.JsonSerializer.Serialize(module) + ";\n" + """
            import assert from 'node:assert/strict';
            async function* chunks(buffer, width) {
              for (let i=0; i<buffer.length; i+=width) yield buffer.subarray(i,i+width);
            }
            async function read(buffer,width=1) {
              const result=[]; for await (const value of decodeFrames(chunks(buffer,width))) result.push(value);
              return result;
            }
            const values=[{text:'Привет 😊'}, {text:'x'.repeat(40000)}, {done:true}];
            const wire=Buffer.concat([...values.map(v=>envelope(v)),envelope({},2)]);
            for (const width of [1,3,8192,wire.length]) assert.deepEqual(await read(wire,width),values);
            const fb=new FrameBuffer(8);
            fb.append(Buffer.from('abcdef')); fb.consume(4); fb.append(Buffer.from('ghijkl'));
            assert.equal(fb.slice(0,8).toString(),'efghijkl');
            fb.consume(8); fb.append(new Uint8Array([1,2,3])); assert.equal(fb.length,3);
            await assert.rejects(read(envelope({x:1}).subarray(0,6)),/Truncated/);
            await assert.rejects(read(envelope({x:1})),/without a terminal frame/);
            await assert.rejects(read(Buffer.alloc(0)),/without a terminal frame/);
            await assert.rejects(read(Buffer.from([0,0,122,18,1])),/too large/);
            await assert.rejects(read(envelope({},1)),/flags/);
            await assert.rejects(read(Buffer.from([0,0,0,0,1,123])),/Malformed/);
            await assert.rejects(read(envelope({error:{code:'resource_exhausted'}},2)),e=>e.status===429);
            console.log('frames-ok');
            """;
        var file = Path.Combine(fixture.Root.FullName, "fixture.mjs");
        await File.WriteAllTextAsync(file, script);
        var start = new System.Diagnostics.ProcessStartInfo(fixture.Node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = fixture.Root.FullName
        };
        start.ArgumentList.Add(file);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("", await stderr);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("frames-ok", await stdout);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task ExpiredConsentRefusesBeforeCreatingNodeProcess()
    {
        using var fixture = new Fixture("import fs from 'node:fs'; fs.writeFileSync('started','unexpected');");
        await Assert.ThrowsAsync<LLMWorkGUI.Application.Security.EgressApprovalException>(() =>
            fixture.Transport.ReviewAsync(fixture.Node, "review", DateTimeOffset.UtcNow.AddSeconds(-1), default));
        Assert.False(File.Exists(Path.Combine(fixture.Root.FullName, "started")));
    }
    [Fact]
    public async Task FailureSurfacesCleanupFlagButNotUpstreamErrorText()
    {
        using var fixture = new Fixture("process.stdin.once('data', () => { console.log(JSON.stringify({ error: 'PRIVATE_UPSTREAM_TEXT', cleanupPending: true })); process.exitCode=1; process.stdin.destroy(); });");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Transport.ReviewAsync(fixture.Node, "test", default));
        Assert.Contains("Очистка временного агента", error.Message);
        Assert.DoesNotContain("PRIVATE_UPSTREAM_TEXT", error.Message);
    }

    [Fact]
    public async Task CancellationWaitsForCooperativeCleanupAndUnqualifiedLaunchRefuses()
    {
        using var fixture = new Fixture("import fs from 'node:fs'; import readline from 'node:readline'; const lines=readline.createInterface({input:process.stdin}); lines.on('line', line => { if(line==='cancel') { fs.writeFileSync('cleaned','ok'); lines.close(); process.stdin.destroy(); } else fs.writeFileSync('started','ok'); });");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Transport.ReviewAsync("node", "test", default));
        using var cancel = new CancellationTokenSource();
        var pending = fixture.Transport.ReviewAsync(fixture.Node, "test", cancel.Token);
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Path.Combine(fixture.Root.FullName, "started")) && DateTime.UtcNow < timeout) await Task.Delay(20);
        Assert.True(File.Exists(Path.Combine(fixture.Root.FullName, "started")));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(File.Exists(Path.Combine(fixture.Root.FullName, "cleaned")));
    }

    [Fact]
    public async Task IgnoredCooperativeCancellationKillsOnlyOwnedProcessWithinBound()
    {
        using var fixture = new Fixture("import fs from 'node:fs'; process.stdin.on('data', () => fs.writeFileSync('pid', String(process.pid))); setInterval(() => {},1000);");
        using var cancel = new CancellationTokenSource();
        var pending = fixture.Transport.ReviewAsync(fixture.Node, "test", cancel.Token);
        try
        {
            var marker = Path.Combine(fixture.Root.FullName, "pid");
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(marker) && DateTime.UtcNow < timeout) await Task.Delay(20);
            Assert.True(File.Exists(marker));
            var pid = int.Parse(await File.ReadAllTextAsync(marker));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(25)));
            try { using var child = System.Diagnostics.Process.GetProcessById(pid); Assert.True(child.HasExited); }
            catch (ArgumentException) { /* Windows already released the owned process object. */ }
        }
        finally
        {
            cancel.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(25)); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("surrogate")]
    [InlineData("empty")]
    [InlineData("valid")]
    [InlineData("expired")]
    [InlineData("invalid-deadline")]
    public async Task BundledBridgeValidatesExactTextBeforeBotAsk(string mode)
    {
        using var fixture = new Fixture("");
        var bridge = Path.Combine(fixture.Root.FullName, "fixture.mjs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "GrokBot", "review-bridge.mjs"), bridge, overwrite: true);
        // Only the bridge is real; the desktop/session/network module is replaced by a local canary.
        await File.WriteAllTextAsync(Path.Combine(fixture.Root.FullName, "bot.mjs"),
            "import fs from 'node:fs'; export const usage=async()=>({}); export const createBot=()=>({ask:async text=>{fs.writeFileSync('called.txt',text,'utf8');return {text:'review'};}});");
        var text = mode is "valid" or "expired" or "invalid-deadline" ? new string('\u4e2d', 64_000) : new string('x', 64_001);
        var body = mode switch
        {
            "surrogate" => "{\"prompt\":\"synthetic-canary-\\ud800\"}",
            "empty" => "{\"prompt\":\"   \"}",
            "expired" => System.Text.Json.JsonSerializer.Serialize(new { prompt = text, notAfterUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O") }),
            "invalid-deadline" => System.Text.Json.JsonSerializer.Serialize(new { prompt = text, notAfterUtc = "not a date" }),
            _ => System.Text.Json.JsonSerializer.Serialize(new { prompt = text })
        };
        var start = new System.Diagnostics.ProcessStartInfo(fixture.Node) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = fixture.Root.FullName };
        start.ArgumentList.Add(bridge);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(body);
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var output = await stdout; Assert.Empty(await stderr);
            var canary = Path.Combine(fixture.Root.FullName, "called.txt");
            if (mode == "valid")
            {
                Assert.Equal(0, process.ExitCode); Assert.Contains("review", output);
                Assert.Equal(text, await File.ReadAllTextAsync(canary));
            }
            else
            {
                Assert.Equal(1, process.ExitCode); Assert.Contains("review_failed", output);
                Assert.DoesNotContain("synthetic-canary", output); Assert.False(File.Exists(canary));
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("grokbot-transport-");
        public string Node { get; } = new ExecutableResolver().Resolve("node")?.FileName ?? throw new InvalidOperationException("Node.js required");
        public GrokBotReviewTransport Transport { get; }
        public Fixture(string script)
        {
            var file = Path.Combine(Root.FullName, "fixture.mjs"); File.WriteAllText(file, script);
            Transport = new(file);
        }
        public void Dispose() => Root.Delete(true);
    }
}

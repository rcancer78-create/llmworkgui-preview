using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessSpoolRedactionTests
{
    // Synthetic diagnostics only. The child transports exactly these bytes; disk is the boundary under test.
    private const string Diagnostics = "before\r\nAuthorization: Bearer syntheticAuthorizationValue\r\n"
        + "key=sk-syntheticSpoolCredential012345\r\n{\r\n  \"\\u0070assword\": \"tiny\",\r\n  \"count\": 1\r\n}\r\n"
        + "-----BEGIN PRIVATE KEY-----\r\nsyntheticPrivateBody\r\n-----END PRIVATE KEY-----\r\nafter 😀\r\n";

    [Theory]
    [InlineData(16, false)]
    [InlineData(4096, false)]
    [InlineData(16, true)]
    public async Task DiskSpoolsRedactCompleteRecordsAcrossReadsWithoutChangingWire(int bufferSize, bool protocol)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        var specification = await CreateEmitterAsync(checkout.Root, Diagnostics, protocol);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        { StreamReadBufferSize = bufferSize }), new StorageOptions { AppDataDirectory = data.Root });
        if (protocol)
        {
            await using var session = await supervisor.StartProtocolProcessAsync(specification);
            using var output = new StreamReader(session.StandardOutput, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(Diagnostics, await output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Encoding.UTF8.GetByteCount(Diagnostics), result.StandardErrorBytes);
            AssertSanitized(await File.ReadAllTextAsync(session.StandardErrorLogPath));
        }
        else
        {
            var result = await supervisor.ExecuteAsync(specification).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Diagnostics, result.StandardOutputHead);
            AssertSanitized(await File.ReadAllTextAsync(result.StandardOutputLogPath));
            AssertSanitized(await File.ReadAllTextAsync(result.StandardErrorLogPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedRecordsNeverPersistAPartialSecretAndContinueDraining(bool protocol)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        var payload = "prefix " + new string('x', 100_000) + " password=syntheticOversizedTail\r\nclean-tail\r\n";
        var specification = await CreateEmitterAsync(checkout.Root, payload, protocol);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        { StreamReadBufferSize = 16 }), new StorageOptions { AppDataDirectory = data.Root });
        string path;
        if (protocol)
        {
            await using var session = await supervisor.StartProtocolProcessAsync(specification);
            using var output = new StreamReader(session.StandardOutput, Encoding.UTF8, leaveOpen: true);
            Assert.Equal(payload, await output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Encoding.UTF8.GetByteCount(payload), result.StandardErrorBytes);
            Assert.False(result.OutputCaptureIncomplete);
            Assert.False(result.OutputLimitExceeded);
            path = session.StandardErrorLogPath;
        }
        else
        {
            var result = await supervisor.ExecuteAsync(specification).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.OutputCaptureIncomplete);
            Assert.False(result.OutputLimitExceeded);
            path = result.StandardOutputLogPath;
        }
        var disk = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("syntheticOversizedTail", disk, StringComparison.Ordinal);
        Assert.Contains("clean-tail", disk, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", disk, StringComparison.Ordinal);
        Assert.True(disk.Length < 1000);
    }

    private static void AssertSanitized(string disk)
    {
        foreach (var secret in new[] { "syntheticAuthorizationValue", "sk-syntheticSpoolCredential012345", "tiny", "syntheticPrivateBody" })
            Assert.DoesNotContain(secret, disk, StringComparison.Ordinal);
        Assert.Contains("before", disk, StringComparison.Ordinal);
        Assert.Contains("after 😀", disk, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", disk, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsPriorCompleteLineButNeverPartialSecret(bool protocol)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        const string payload = "prior-complete\n{\"password\":\"syntheticPartialSecret";
        var specification = await CreateEmitterAsync(checkout.Root, payload, protocol, waitForCancellation: true);
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        { StreamReadBufferSize = 16, GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100) }),
            new StorageOptions { AppDataDirectory = data.Root });
        string path;
        if (protocol)
        {
            await using var session = await supervisor.StartProtocolProcessAsync(specification);
            using var output = new StreamReader(session.StandardOutput, Encoding.UTF8, leaveOpen: true);
            Assert.Equal("prior-complete", await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var line = await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.EndsWith("owned-fixture-ready", line!, StringComparison.Ordinal);
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            path = session.StandardErrorLogPath;
        }
        else
        {
            using var cancel = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new StringBuilder();
            var progress = new CallbackProgress<ProcessOutputEvent>(item =>
            {
                if (item.StreamKind != ProcessStreamKind.StdOut) return;
                observed.Append(item.Text);
                if (observed.ToString().Contains("owned-fixture-ready", StringComparison.Ordinal)) ready.TrySetResult();
            });
            var running = supervisor.ExecuteAsync(specification, progress, cancel.Token);
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                cancel.Cancel();
                var result = await running.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(ProcessTerminationReason.UserCancelled, result.TerminationReason);
                path = result.StandardOutputLogPath;
            }
            finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(20)); }
        }
        var disk = await File.ReadAllTextAsync(path);
        Assert.Contains("prior-complete\n", disk, StringComparison.Ordinal);
        Assert.DoesNotContain("syntheticPartialSecret", disk, StringComparison.Ordinal);
    }

    private static async Task<ProcessStartSpecification> CreateEmitterAsync(string checkout, string payload, bool protocol, bool waitForCancellation = false)
    {
        // The payload is in a fixture file, avoiding the Windows environment size limit for long records.
        var input = Path.Combine(checkout, "synthetic-diagnostics.txt");
        await File.WriteAllTextAsync(input, payload, new UTF8Encoding(false));
        var script = Path.Combine(checkout, "emit-diagnostics.ps1");
        await File.WriteAllTextAsync(script,
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); "
            + "$text = [IO.File]::ReadAllText($env:LLMWORKGUI_DIAGNOSTIC_FIXTURE, [Text.Encoding]::UTF8); "
            + "[Console]::Out.Write($text); [Console]::Out.Flush(); [Console]::Error.Write($text); [Console]::Error.Flush();"
            + (waitForCancellation ? " [Console]::Out.WriteLine('owned-fixture-ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 120;" : ""),
            new UTF8Encoding(true));
        return new ProcessStartSpecification
        {
            ExecutionId = "spool-redaction-" + Guid.NewGuid().ToString("N"), FileName = "powershell.exe",
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            WorkingDirectory = checkout, EnvironmentVariables = new Dictionary<string, string> { ["LLMWORKGUI_DIAGNOSTIC_FIXTURE"] = input },
            StdinPolicy = protocol ? ProcessStdinPolicy.DirectProtocolTransport : ProcessStdinPolicy.Closed
        };
    }
}

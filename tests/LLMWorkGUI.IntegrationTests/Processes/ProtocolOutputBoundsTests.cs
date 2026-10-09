using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProtocolOutputBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStderrSinkStillDrainsOwnedProcessAndReportsIncompleteCapture(bool failOpening)
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        var script = Path.Combine(checkout.Root, "stderr-sink-failure.ps1");
        await File.WriteAllTextAsync(script,
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
            "$line = 'x' * 8192; for ($index = 0; $index -lt 256; $index++) { [Console]::Error.Write($line) }; " +
            "[Console]::Out.Write('drained-after-sink-failure');", new UTF8Encoding(true));
        var sink = new FailingSink();
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            ProtocolStandardErrorSpoolLimitBytes = 4 * 1024 * 1024,
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100)
        }), new StorageOptions { AppDataDirectory = data.Root }, null, null,
            static process => process.Start(), protocolStandardErrorSinkFactory: _ => failOpening
                ? throw new IOException("synthetic stderr sink open failure") : sink);
        var session = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "protocol-stderr-sink-failure-" + failOpening, FileName = "powershell.exe",
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            WorkingDirectory = checkout.Root, StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });
        try
        {
            using var output = new StreamReader(session.StandardOutput, leaveOpen: true);
            Assert.Equal("drained-after-sink-failure", await output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(8192L * 256, result.StandardErrorBytes);
            Assert.True(result.OutputOverflowed);
            Assert.True(result.OutputCaptureIncomplete);
            Assert.False(result.OutputLimitExceeded);
            Assert.Equal(failOpening ? 0 : 1, sink.WriteCalls);
            Assert.False(session.IsRunning);
        }
        finally { await session.StopAsync(); await session.DisposeAsync(); }
    }

    [Theory]
    [InlineData(1024, "ascii")]
    [InlineData(1025, "unicode")]
    public async Task ProtocolStderrSpoolRemainsBoundedWhileDrainingAllOutput(long limit, string content)
    {
        using var checkout = new TestDirectory();
        using var directory = new TestDirectory();
        var text = content == "ascii" ? new string('x', 8192) : string.Concat(Enumerable.Repeat("€😀", 2048));
        var script = Path.Combine(checkout.Root, "emit-stderr.ps1");
        await File.WriteAllTextAsync(script,
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
            "[Console]::Error.Write('" + text + "'); [Console]::Out.Write('protocol-tail');", new UTF8Encoding(true));
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            ProtocolStandardErrorSpoolLimitBytes = limit,
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100)
        }), new StorageOptions { AppDataDirectory = directory.Root });
        await using var session = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "bounded-protocol-stderr-" + content,
            FileName = "powershell.exe",
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            WorkingDirectory = checkout.Root,
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });
        using var stdout = new StreamReader(session.StandardOutput, leaveOpen: true);
        Assert.Equal("protocol-tail", await stdout.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardErrorBytes > limit);
        Assert.InRange(new FileInfo(session.StandardErrorLogPath).Length, 1, limit);
        Assert.True(result.OutputOverflowed);
        Assert.False(result.OutputCaptureIncomplete);
        Assert.True(result.OutputLimitExceeded);
        Assert.Equal(string.Empty, result.StandardOutputLogPath);
        Assert.False(File.Exists(Path.Combine(session.RunDirectory, "stdout.log")));
        var prefix = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(session.StandardErrorLogPath));
        Assert.StartsWith(prefix, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProtocolSpoolLimitMustBePositive(long limit)
    {
        var result = new ProcessSupervisorOptionsValidator().Validate(null,
            new ProcessSupervisorOptions { ProtocolStandardErrorSpoolLimitBytes = limit });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(nameof(ProcessSupervisorOptions.ProtocolStandardErrorSpoolLimitBytes)));
    }

    private sealed class FailingSink : MemoryStream
    {
        public int WriteCalls { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return ValueTask.FromException(new IOException("synthetic stderr sink failure"));
        }
    }
}

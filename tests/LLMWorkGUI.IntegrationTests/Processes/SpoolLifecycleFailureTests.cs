using System.Reflection;
using System.Text;
using System.Threading.Channels;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class SpoolLifecycleFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbortBeforeEofMarksIncompleteButNormalEofDoesNot(bool protocol, bool stall)
    {
        using var data = new TestDirectory();
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = data.Root });
        using var input = new ControlledPipe(stall);
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var abort = new CancellationTokenSource();
        var observer = new CaptureObserver();
        var channel = Channel.CreateUnbounded<ProcessOutputEvent>();
        var path = Path.Combine(data.Root, "capture.log");
        var method = typeof(ProcessSupervisor).GetMethod(protocol ? "SpoolStandardErrorAsync" : "ReadStreamAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(supervisor, protocol
            ? [reader, path, (Action<int>)(_ => { }), (Action)(() => observer.OnOutputCaptureFailure(ProcessStreamKind.StdErr)), abort.Token]
            : [reader, ProcessStreamKind.StdErr, path, channel.Writer, (Action)(() => { }), observer, abort.Token])!;
        if (stall)
        {
            await input.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            abort.Cancel();
        }
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(stall ? 1 : 0, observer.Failures);
        if (!protocol) Assert.Equal(stall, await (Task<bool>)task);
        Assert.Equal("complete-before-eof\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ProtocolSinkDisposalFailureSetsIncompleteOnActualOwnedProcess()
    {
        using var checkout = new TestDirectory();
        using var data = new TestDirectory();
        var script = Path.Combine(checkout.Root, "dispose-sink.ps1");
        await File.WriteAllTextAsync(script, "[Console]::Error.WriteLine('complete'); [Console]::Out.Write('wire-tail');", new UTF8Encoding(true));
        var sink = new DisposeFailingSink();
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions()),
            new StorageOptions { AppDataDirectory = data.Root }, null, null, static p => p.Start(),
            protocolStandardErrorSinkFactory: _ => sink);
        await using var session = await supervisor.StartProtocolProcessAsync(new ProcessStartSpecification
        {
            ExecutionId = "protocol-dispose-fault", FileName = "powershell.exe",
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script],
            WorkingDirectory = checkout.Root, StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        });
        using var output = new StreamReader(session.StandardOutput, leaveOpen: true);
        Assert.Equal("wire-tail", await output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        Assert.True(sink.DisposeAttempted);
        Assert.True(result.OutputCaptureIncomplete);
        Assert.True(result.OutputOverflowed);
        Assert.False(result.OutputLimitExceeded);
    }

    private sealed class CaptureObserver : IProcessOutputCaptureObserver
    {
        public int Failures;
        public void OnOutputCaptureFailure(ProcessStreamKind streamKind) => Interlocked.Increment(ref Failures);
    }
    private sealed class DisposeFailingSink : MemoryStream
    {
        public bool DisposeAttempted;
        public override ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            base.Dispose();
            return ValueTask.FromException(new IOException("synthetic deferred close failure"));
        }
    }
    private sealed class ControlledPipe(bool stall) : Stream
    {
        private bool _emitted;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_emitted)
            {
                _emitted = true;
                var bytes = Encoding.UTF8.GetBytes("complete-before-eof\n");
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
            if (!stall) return 0;
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

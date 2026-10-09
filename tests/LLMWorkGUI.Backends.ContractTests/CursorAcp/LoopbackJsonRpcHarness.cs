using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// In-process loopback of the two stdio pipes of a fake ACP agent. The transport writes frames to
/// <see cref="TransportOutput"/> and reads frames from <see cref="TransportInput"/>; the fake agent
/// reads and writes the opposite ends, optionally answering through <see cref="FrameHandler"/>.
/// </summary>
internal sealed class LoopbackJsonRpcHarness : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AnonymousPipeServerStream _agentInput;
    private readonly AnonymousPipeClientStream _agentInputClient;
    private readonly AnonymousPipeServerStream _agentOutput;
    private readonly AnonymousPipeClientStream _agentOutputClient;
    private readonly StreamReader _agentReader;
    private readonly StreamWriter _agentWriter;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<string> _receivedFrames = Channel.CreateUnbounded<string>();
    private readonly Task _agentLoop;

    public LoopbackJsonRpcHarness()
    {
        _agentInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        _agentInputClient = new AnonymousPipeClientStream(PipeDirection.In, _agentInput.ClientSafePipeHandle);
        _agentOutput = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        _agentOutputClient = new AnonymousPipeClientStream(PipeDirection.Out, _agentOutput.ClientSafePipeHandle);

        _agentReader = new StreamReader(_agentInputClient, Utf8NoBom);
        _agentWriter = new StreamWriter(_agentOutputClient, Utf8NoBom) { AutoFlush = true };

        _agentLoop = Task.Run(AgentLoopAsync);
    }

    public Func<string, string?>? FrameHandler { get; set; }

    public Stream TransportInput => _agentOutput;

    public Stream TransportOutput => _agentInput;

    public async Task<string> ReadFrameAsync(TimeSpan timeout) =>
        await _receivedFrames.Reader.ReadAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);

    public async Task WriteRawLineAsync(string line)
    {
        await _agentWriter.WriteLineAsync(line).ConfigureAwait(false);
        await _agentWriter.FlushAsync().ConfigureAwait(false);
    }

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new TimeoutException("The expected condition was not observed in time.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        try
        {
            await _agentLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _agentReader.Dispose();
        await _agentWriter.DisposeAsync().ConfigureAwait(false);
        _agentInputClient.Dispose();
        _agentOutputClient.Dispose();
        await _agentInput.DisposeAsync().ConfigureAwait(false);
        await _agentOutput.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task AgentLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                string? line;

                try
                {
                    line = await _agentReader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                _receivedFrames.Writer.TryWrite(line);

                var handler = FrameHandler;

                if (handler is null)
                {
                    continue;
                }

                var reply = handler(line);

                if (reply is null)
                {
                    continue;
                }

                await _agentWriter.WriteLineAsync(reply.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                await _agentWriter.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _receivedFrames.Writer.TryComplete();
        }
    }
}

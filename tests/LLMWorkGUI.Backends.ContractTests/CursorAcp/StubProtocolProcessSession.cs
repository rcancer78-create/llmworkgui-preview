using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// In-memory protocol process session used by contract tests. It exposes real in-memory duplex
/// streams so a JSON-RPC transport can be bound exactly as it is against a live process, without
/// starting any OS process.
/// </summary>
internal sealed class StubProtocolProcessSession : IProtocolProcessSession
{
    private readonly TaskCompletionSource<ProcessExecutionResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _handlerTask;
    private readonly ProcessExecutionResult? _terminalResult;

    private bool _stopped;
    private bool _disposed;

    internal StubProtocolProcessSession(
        ProcessStartSpecification specification,
        ProcessExecutionResult? terminalResult,
        Task handlerTask)
    {
        Specification = specification;
        ExecutionId = specification.ExecutionId;
        RunDirectory = "stub-run";
        StandardErrorLogPath = Path.Combine("stub-run", "stderr.log");
        StartedAtUtc = DateTimeOffset.UnixEpoch;

        _terminalResult = terminalResult;
        _handlerTask = handlerTask;

        // Agent stdout is what the transport reads; agent stdin is what the transport writes.
        AgentStandardOutputWriter = new MemoryStream();
        StandardOutput = new MemoryStream();
        StandardInput = new MemoryStream();

        if (terminalResult is not null)
        {
            _completion.TrySetResult(terminalResult);
        }
    }

    public ProcessStartSpecification Specification { get; }

    public string ExecutionId { get; }

    public int ProcessId => 4242;

    public long ProcessGeneration => 73;

    public string RunDirectory { get; }

    public string StandardErrorLogPath { get; }

    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Writable side used by tests to inject agent output frames.</summary>
    public MemoryStream AgentStandardOutputWriter { get; }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Task<ProcessExecutionResult> Completion => _completion.Task;

    /// <summary>True once <see cref="StopAsync"/> or disposal ran.</summary>
    public bool StopRequested => _stopped;

    public bool IsRunning => _terminalResult is null && !_stopped;

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopped = true;

        _completion.TrySetResult(_terminalResult ?? StubProcessSupervisor.CreateResult(
            Specification,
            exitCode: 0,
            standardOutput: string.Empty,
            standardError: string.Empty,
            terminationReason: ProcessTerminationReason.UserCancelled));

        _ = _handlerTask.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        await StandardInput.DisposeAsync().ConfigureAwait(false);
        await StandardOutput.DisposeAsync().ConfigureAwait(false);
        await AgentStandardOutputWriter.DisposeAsync().ConfigureAwait(false);
    }
}

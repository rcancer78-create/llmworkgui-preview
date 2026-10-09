using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

internal sealed class StubProcessSupervisor : IProcessSupervisor
{
    private readonly Func<ProcessStartSpecification, CancellationToken, Task<ProcessExecutionResult>> _handler;

    public StubProcessSupervisor(
        Func<ProcessStartSpecification, CancellationToken, Task<ProcessExecutionResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    public List<ProcessStartSpecification> Specifications { get; } = new();

    public ProcessStartSpecification LastSpecification =>
        Specifications.Count > 0
            ? Specifications[^1]
            : throw new InvalidOperationException("No process specification was captured.");

    /// <summary>Protocol sessions handed out by <see cref="StartProtocolProcessAsync"/>.</summary>
    public List<StubProtocolProcessSession> ProtocolSessions { get; } = new();

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessStartSpecification specification,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default)
    {
        Specifications.Add(specification);

        return _handler(specification, cancellationToken);
    }

    /// <summary>
    /// Emulates the duplex protocol launch: the captured specification is recorded exactly as the
    /// real supervisor would receive it, and the handler result decides whether the stub reports a
    /// running process, an immediate exit or a launch rejection.
    /// </summary>
    public async Task<IProtocolProcessSession> StartProtocolProcessAsync(
        ProcessStartSpecification specification,
        CancellationToken cancellationToken = default)
    {
        Specifications.Add(specification);

        var handlerTask = _handler(specification, cancellationToken);

        // A handler that never completes represents a live long-running process.
        var completed = await Task.WhenAny(handlerTask, Task.Delay(50, cancellationToken))
            .ConfigureAwait(false);

        ProcessExecutionResult? terminalResult = null;

        if (completed == handlerTask)
        {
            // Propagates launch rejections (Throwing) exactly like the real supervisor.
            terminalResult = await handlerTask.ConfigureAwait(false);
        }

        var session = new StubProtocolProcessSession(specification, terminalResult, handlerTask);
        ProtocolSessions.Add(session);

        return session;
    }

    public static StubProcessSupervisor Returning(
        int? exitCode = 0,
        string standardOutput = "",
        string standardError = "",
        ProcessTerminationReason terminationReason = ProcessTerminationReason.None) =>
        new((specification, _) => Task.FromResult(CreateResult(
            specification,
            exitCode,
            standardOutput,
            standardError,
            terminationReason)));

    public static StubProcessSupervisor Hanging() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("Unreachable.");
        });

    public static StubProcessSupervisor ReturningStartupFailure(string failureMessage) =>
        new((specification, _) => Task.FromResult(CreateResult(
                specification,
                exitCode: null,
                standardOutput: string.Empty,
                standardError: string.Empty,
                terminationReason: ProcessTerminationReason.StartupTimeout)
            with
            {
                FailureMessage = failureMessage
            }));

    public static StubProcessSupervisor Throwing(Exception exception) =>
        new((_, _) => throw exception);

    public static ProcessExecutionResult CreateResult(
        ProcessStartSpecification specification,
        int? exitCode,
        string standardOutput,
        string standardError,
        ProcessTerminationReason terminationReason = ProcessTerminationReason.None) =>
        new()
        {
            ExecutionId = specification.ExecutionId,
            ProcessId = 4242,
            TerminationReason = terminationReason,
            ExitCode = exitCode,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            ExitedAtUtc = DateTimeOffset.UnixEpoch,
            RunDirectory = "stub-run",
            StandardOutputLogPath = "stub-run/stdout.log",
            StandardErrorLogPath = "stub-run/stderr.log",
            StandardOutputBytes = standardOutput.Length,
            StandardErrorBytes = standardError.Length,
            StandardOutputHead = standardOutput,
            StandardOutputTail = string.Empty,
            StandardErrorHead = standardError,
            StandardErrorTail = string.Empty,
            OutputOverflowed = false
        };
}

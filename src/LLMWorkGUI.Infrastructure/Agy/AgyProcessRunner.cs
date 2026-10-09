using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Runs the standard agy CLI directly with the project cwd, model, mode
/// (<c>--mode</c>), bounded timeout and machine-readable stream
/// (<c>--input-format text --output-format stream-json</c>). Cancellation and timeout
/// terminate the supervised process tree through the shared process supervisor
/// (ProcessTreeTerminator) and no OpenCode plugin fallback exists (ТЗ §6.11a).
/// </summary>
public sealed class AgyProcessRunner : IAgyProcessRunner
{
    public const string AgyExecutableName = "agy";

    public const string InputFormatText = "text";

    public const string OutputFormatStreamJson = "stream-json";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessSupervisor _processSupervisor;
    private readonly ICliExecutableLocator _executableLocator;
    private readonly ILogger<AgyProcessRunner> _logger;

    public AgyProcessRunner(
        IProcessSupervisor processSupervisor,
        ICliExecutableLocator executableLocator,
        ILogger<AgyProcessRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(executableLocator);

        _processSupervisor = processSupervisor;
        _executableLocator = executableLocator;
        _logger = logger ?? NullLogger<AgyProcessRunner>.Instance;
    }

    public async Task<AgyRunResult> RunAsync(
        AgyRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mode);

        if (!IsSupportedMode(request.Mode))
        {
            return Failure(
                parser: null,
                processResult: null,
                $"Unsupported AGY mode '{request.Mode}'. Supported modes: " +
                $"'{AgyRunRequest.ModeAcceptEdits}', '{AgyRunRequest.ModePlan}'.");
        }

        if (!Directory.Exists(request.ProjectDirectory))
        {
            return Failure(
                parser: null,
                processResult: null,
                $"AGY project directory '{request.ProjectDirectory}' does not exist.");
        }

        var executablePath = await _executableLocator
            .LocateAsync(AgyExecutableName, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Failure(
                parser: null,
                processResult: null,
                $"The standard agy CLI ('{AgyExecutableName}') was not found on PATH; the native AGY route " +
                "is unavailable and no OpenCode plugin fallback is permitted (ТЗ §6.11a).");
        }

        var timeout = request.Timeout ?? DefaultTimeout;
        var parser = new AgyStreamJsonParser();
        var outputProgress = new SynchronousOutputProgress(parser.Append);
        var specification = BuildSpecification(request, executablePath, timeout);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        ProcessExecutionResult? processResult;
        try
        {
            processResult = await _processSupervisor
                .ExecuteAsync(specification, outputProgress, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            parser.Complete();

            var failureReason = cancellationToken.IsCancellationRequested
                ? "AGY execution was cancelled by the user; the process tree was terminated."
                : $"AGY execution exceeded the configured timeout of {timeout}; the process tree was terminated.";

            _logger.LogWarning(
                "AGY execution {ExecutionId} ended by cancellation/timeout for model {ModelId}.",
                request.ExecutionId,
                request.ModelId);

            return Failure(parser, processResult: null, failureReason);
        }

        parser.Complete();
        return EvaluateOutcome(request, parser, processResult, timeout);
    }

    private AgyRunResult EvaluateOutcome(
        AgyRunRequest request,
        AgyStreamJsonParser parser,
        ProcessExecutionResult processResult,
        TimeSpan timeout)
    {
        if (processResult.TerminationReason != ProcessTerminationReason.None)
        {
            return Failure(
                parser,
                processResult,
                $"AGY execution was terminated abnormally ({processResult.TerminationReason}) " +
                $"after {processResult.Duration}; no result is accepted.");
        }

        if (processResult.ExitCode != 0)
        {
            return Failure(
                parser,
                processResult,
                $"agy exited with code {processResult.ExitCode?.ToString() ?? "(none)"}; exit code 0 is required.");
        }

        if (!parser.InitConfirmed)
        {
            return Failure(
                parser,
                processResult,
                "The agy stream did not confirm session init; a live PID or exit code alone is not a successful run (ТЗ §6.11a).");
        }

        if (parser.TerminalResult is null)
        {
            return Failure(
                parser,
                processResult,
                "The agy stream ended without a terminal result event; a partial stream is never treated as success (ТЗ §6.11a).");
        }

        if (parser.TerminalResult.IsError)
        {
            return Failure(
                parser,
                processResult,
                $"The agy terminal result reported an error" +
                $"{(string.IsNullOrWhiteSpace(parser.TerminalResult.Subtype) ? string.Empty : $" ({parser.TerminalResult.Subtype})")}.");
        }

        _logger.LogInformation(
            "AGY execution {ExecutionId} completed with conversation {ConversationId} on model {ObservedModel}.",
            request.ExecutionId,
            parser.ConversationId,
            parser.ObservedModel ?? request.ModelId);

        return Success(parser, processResult);
    }

    private static ProcessStartSpecification BuildSpecification(
        AgyRunRequest request,
        string executablePath,
        TimeSpan timeout)
    {
        var arguments = new List<string>
        {
            "--print",
            request.Prompt,
            "--input-format",
            InputFormatText,
            "--output-format",
            OutputFormatStreamJson,
            "--mode",
            request.Mode,
            "--model",
            request.ModelId,
            "--print-timeout",
            FormatPrintTimeout(timeout)
        };

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            arguments.Add("--conversation");
            arguments.Add(request.ConversationId);
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            arguments.Add("--effort");
            arguments.Add(request.ReasoningEffort);
        }

        return new ProcessStartSpecification
        {
            ExecutionId = request.ExecutionId,
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = request.ProjectDirectory,
            InheritEnvironment = false,
            EnvironmentVariables = AgyProcessEnvironment.Create(executablePath),
            StdinPolicy = ProcessStdinPolicy.Closed
        };
    }

    private static string FormatPrintTimeout(TimeSpan timeout) =>
        $"{(long)Math.Ceiling(Math.Max(1, timeout.TotalSeconds))}s";

    private static bool IsSupportedMode(string mode) =>
        string.Equals(mode, AgyRunRequest.ModeAcceptEdits, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, AgyRunRequest.ModePlan, StringComparison.OrdinalIgnoreCase);

    private static AgyRunResult Success(AgyStreamJsonParser parser, ProcessExecutionResult processResult) => new()
    {
        IsSuccess = true,
        FailureReason = null,
        InitConfirmed = parser.InitConfirmed,
        ConversationId = parser.ConversationId,
        ObservedModel = parser.ObservedModel,
        GrantedPermissions = parser.GrantedPermissions.ToArray(),
        ToolEvents = parser.ToolEvents.ToArray(),
        TerminalResult = parser.TerminalResult,
        ProcessResult = processResult
    };

    private static AgyRunResult Failure(
        AgyStreamJsonParser? parser,
        ProcessExecutionResult? processResult,
        string failureReason) => new()
    {
        IsSuccess = false,
        FailureReason = failureReason,
        InitConfirmed = parser?.InitConfirmed ?? false,
        ConversationId = parser?.ConversationId,
        ObservedModel = parser?.ObservedModel,
        GrantedPermissions = parser?.GrantedPermissions.ToArray() ?? Array.Empty<string>(),
        ToolEvents = parser?.ToolEvents.ToArray() ?? Array.Empty<AgyToolEvent>(),
        TerminalResult = parser?.TerminalResult,
        ProcessResult = processResult
    };

    /// <summary>
    /// Synchronous progress sink so every stream-json line is parsed before the supervised
    /// execution completes. <see cref="Progress{T}"/> would post asynchronously and could
    /// drop the terminal event.
    /// </summary>
    private sealed class SynchronousOutputProgress : IProgress<ProcessOutputEvent>
    {
        private readonly Action<string> _onStandardOutput;

        public SynchronousOutputProgress(Action<string> onStandardOutput)
        {
            ArgumentNullException.ThrowIfNull(onStandardOutput);
            _onStandardOutput = onStandardOutput;
        }

        public void Report(ProcessOutputEvent value)
        {
            if (value.StreamKind == ProcessStreamKind.StdOut)
            {
                _onStandardOutput(value.Text);
            }
        }
    }
}

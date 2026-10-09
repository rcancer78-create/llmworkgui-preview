using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Infrastructure.Agy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Agy;

public sealed class AgyProcessRunnerTests : IDisposable
{
    private const string FullSuccessfulStream =
        "{\"type\":\"init\",\"conversation_id\":\"conv-123\",\"model\":\"gemini-2.5-pro\"," +
        "\"permissions\":[\"read_file\"],\"permission_mode\":\"accept-edits\"}\n" +
        "{\"type\":\"tool_call\",\"name\":\"read_file\",\"status\":\"allowed\"}\n" +
        "{\"type\":\"tool_result\",\"name\":\"read_file\",\"status\":\"ok\",\"output\":\"TOOL-PAYLOAD-SECRET\"}\n" +
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\"}\n";

    private static readonly DateTimeOffset StartedAt = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task RunAsync_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-agy";
        const string executablePath = @"C:\tools\agy.exe";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var runner = CreateRunner(FullSuccessfulStream, executablePath, out var supervisor, out _);

            var result = await runner.RunAsync(CreateRequest());

            Assert.True(result.IsSuccess);
            var specification = supervisor.Captured;
            Assert.NotNull(specification);
            Assert.False(specification!.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            var baseline = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
            Assert.Equal(baseline["PATH"], specification.EnvironmentVariables["PATH"]);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsPowerShell", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"C:\tools", specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            foreach (var name in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value))
                    Assert.False(specification.EnvironmentVariables.ContainsKey(name));
                else
                    Assert.Equal(value, specification.EnvironmentVariables[name]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAgyIsMissing_ReturnsFailureWithoutAnyFallback()
    {
        var runner = CreateRunner(stdout: string.Empty, executablePath: null, out var supervisor, out var locator);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Contains("was not found on PATH", result.FailureReason);
        Assert.Contains("no OpenCode plugin fallback is permitted", result.FailureReason);
        Assert.Contains("agy", locator.RequestedNames);
        Assert.Equal(0, supervisor.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsUnsupportedMode_BeforeStartingAnything()
    {
        var runner = CreateRunner(stdout: string.Empty, executablePath: @"C:\tools\agy.exe", out var supervisor, out var locator);

        var result = await runner.RunAsync(CreateRequest(mode: "yolo"));

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported AGY mode 'yolo'", result.FailureReason);
        Assert.Empty(locator.RequestedNames);
        Assert.Equal(0, supervisor.CallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingProjectDirectory()
    {
        var runner = CreateRunner(stdout: string.Empty, executablePath: @"C:\tools\agy.exe", out var supervisor, out _);
        var request = new AgyRunRequest
        {
            ExecutionId = "agy-test",
            ProjectDirectory = _directory.GetPath("missing-project"),
            ModelId = "gemini-2.5-pro",
            Mode = AgyRunRequest.ModePlan,
            Prompt = "hello"
        };

        var result = await runner.RunAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains("does not exist", result.FailureReason);
        Assert.Equal(0, supervisor.CallCount);
    }

    [Fact]
    public async Task RunAsync_ParsesMachineReadableStream_AndReturnsConfirmedSuccess()
    {
        var runner = CreateRunner(
            stdout: FullSuccessfulStream,
            executablePath: @"C:\tools\agy.exe",
            out var supervisor,
            out _);

        var result = await runner.RunAsync(CreateRequest());

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.Null(result.FailureReason);
        Assert.True(result.InitConfirmed);
        Assert.Equal("conv-123", result.ConversationId);
        Assert.Equal("gemini-2.5-pro", result.ObservedModel);
        Assert.Contains("read_file", result.GrantedPermissions);
        Assert.Contains("permission-mode:accept-edits", result.GrantedPermissions);
        Assert.Equal(2, result.ToolEvents.Count);
        Assert.Contains(result.ToolEvents, tool => tool.Name == "read_file" && tool.Status == "allowed");
        Assert.Contains(result.ToolEvents, tool => tool.Name == "read_file" && tool.Status == "ok");
        Assert.NotNull(result.TerminalResult);
        Assert.False(result.TerminalResult!.IsError);
        Assert.Equal("success", result.TerminalResult.Subtype);
        Assert.Equal("done", result.TerminalResult.ResultText);
        Assert.NotNull(result.ProcessResult);

        // Only sanitized metadata is retained; tool payloads never surface in the result.
        Assert.DoesNotContain(
            result.ToolEvents,
            tool => (tool.Status ?? string.Empty).Contains("TOOL-PAYLOAD-SECRET", StringComparison.Ordinal));

        var specification = supervisor.Captured!;
        Assert.Equal(@"C:\tools\agy.exe", specification.FileName);
        Assert.Equal(_directory.Root, specification.WorkingDirectory);
        Assert.Equal(ProcessStdinPolicy.Closed, specification.StdinPolicy);

        Assert.Equal("--print", specification.Arguments[0]);
        Assert.Equal("Summarize the repository", specification.Arguments[1]);
        Assert.Contains("--input-format", specification.Arguments);
        Assert.Contains("text", specification.Arguments);
        Assert.Contains("--output-format", specification.Arguments);
        Assert.Contains("stream-json", specification.Arguments);
        Assert.Contains("--mode", specification.Arguments);
        Assert.Contains("plan", specification.Arguments);
        Assert.Contains("--model", specification.Arguments);
        Assert.Contains("--print-timeout", specification.Arguments);
    }

    [Fact]
    public async Task RunAsync_WhenExitCodeIsZeroButStreamIsPartial_Fails()
    {
        var partialStream = "{\"type\":\"init\",\"conversation_id\":\"conv-1\",\"model\":\"gemini-2.5-pro\"}\n";

        var runner = CreateRunner(partialStream, @"C:\tools\agy.exe", out _, out _);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.True(result.InitConfirmed);
        Assert.Contains("without a terminal result event", result.FailureReason);
        Assert.Contains("never treated as success", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WhenInitIsMissing_FailsEvenWithTerminalResult()
    {
        var stream = "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\"}\n";

        var runner = CreateRunner(stream, @"C:\tools\agy.exe", out _, out _);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.False(result.InitConfirmed);
        Assert.Contains("did not confirm session init", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WhenExitCodeIsNonZero_FailsDespiteCompleteStream()
    {
        var runner = CreateRunner(
            FullSuccessfulStream,
            @"C:\tools\agy.exe",
            out _,
            out _,
            exitCode: 2);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Contains("exited with code 2", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WhenTerminalResultReportsError_Fails()
    {
        var stream =
            "{\"type\":\"init\",\"conversation_id\":\"conv-1\",\"model\":\"gemini-2.5-pro\"}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"failed\"}\n";

        var runner = CreateRunner(stream, @"C:\tools\agy.exe", out _, out _);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.TerminalResult);
        Assert.True(result.TerminalResult!.IsError);
        Assert.Contains("terminal result reported an error", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WhenProcessIsTerminatedAbnormally_Fails()
    {
        var runner = CreateRunner(
            FullSuccessfulStream,
            @"C:\tools\agy.exe",
            out _,
            out _,
            terminationReason: ProcessTerminationReason.UnexpectedExit);

        var result = await runner.RunAsync(CreateRequest());

        Assert.False(result.IsSuccess);
        Assert.Contains("terminated abnormally", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutElapses_TerminatesProcessTreeAndFails()
    {
        var runner = CreateRunner(
            stdout: string.Empty,
            executablePath: @"C:\tools\agy.exe",
            out var supervisor,
            out _,
            hangUntilCancelled: true);

        var result = await runner.RunAsync(CreateRequest(timeout: TimeSpan.FromMilliseconds(50)));

        Assert.False(result.IsSuccess);
        Assert.Contains("exceeded the configured timeout", result.FailureReason);
        Assert.Contains("process tree was terminated", result.FailureReason);
        Assert.True(supervisor.CapturedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task RunAsync_WhenCallerCancels_ReportsCancellationAndStops()
    {
        var runner = CreateRunner(
            stdout: string.Empty,
            executablePath: @"C:\tools\agy.exe",
            out var supervisor,
            out _,
            hangUntilCancelled: true);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await runner.RunAsync(CreateRequest(), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Contains("cancelled by the user", result.FailureReason);
        Assert.Equal(1, supervisor.CallCount);
    }

    [Fact]
    public async Task RunAsync_IncludesConversationAndEffortArguments_WhenProvided()
    {
        var runner = CreateRunner(FullSuccessfulStream, @"C:\tools\agy.exe", out var supervisor, out _);

        var result = await runner.RunAsync(CreateRequest(
            conversationId: "conv-previous",
            effort: "high"));

        Assert.True(result.IsSuccess, result.FailureReason);

        var arguments = supervisor.Captured!.Arguments.ToList();
        var conversationIndex = arguments.IndexOf("--conversation");
        Assert.True(conversationIndex >= 0);
        Assert.Equal("conv-previous", arguments[conversationIndex + 1]);

        var effortIndex = arguments.IndexOf("--effort");
        Assert.True(effortIndex >= 0);
        Assert.Equal("high", arguments[effortIndex + 1]);
    }

    [Fact]
    public async Task RunAsync_ValidatesRequiredRequestFields()
    {
        var runner = CreateRunner(FullSuccessfulStream, @"C:\tools\agy.exe", out _, out _);

        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!));

        var missingProject = new AgyRunRequest
        {
            ExecutionId = "agy-test",
            ProjectDirectory = " ",
            ModelId = "gemini-2.5-pro",
            Mode = AgyRunRequest.ModePlan
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => runner.RunAsync(missingProject));
    }

    private AgyProcessRunner CreateRunner(
        string stdout,
        string? executablePath,
        out StubProcessSupervisor supervisor,
        out StubCliExecutableLocator locator,
        int exitCode = 0,
        ProcessTerminationReason terminationReason = ProcessTerminationReason.None,
        bool hangUntilCancelled = false)
    {
        supervisor = new StubProcessSupervisor(stdout, exitCode, terminationReason, hangUntilCancelled);
        locator = new StubCliExecutableLocator { ResolvedPath = executablePath };

        return new AgyProcessRunner(supervisor, locator);
    }

    private AgyRunRequest CreateRequest(
        string mode = AgyRunRequest.ModePlan,
        string? conversationId = null,
        string? effort = null,
        TimeSpan? timeout = null)
    {
        return new AgyRunRequest
        {
            ExecutionId = "agy-test-execution",
            ProjectDirectory = _directory.Root,
            ModelId = "gemini-2.5-pro",
            Mode = mode,
            Prompt = "Summarize the repository",
            ConversationId = conversationId,
            ReasoningEffort = effort,
            Timeout = timeout
        };
    }

    private static ProcessExecutionResult CreateResult(
        string executionId,
        string standardOutput,
        int exitCode,
        ProcessTerminationReason terminationReason)
    {
        return new ProcessExecutionResult
        {
            ExecutionId = executionId,
            ProcessId = 5555,
            TerminationReason = terminationReason,
            ExitCode = exitCode,
            StartedAtUtc = StartedAt,
            ExitedAtUtc = StartedAt.AddSeconds(2),
            RunDirectory = @"C:\runs\agy-test",
            StandardOutputLogPath = @"C:\runs\agy-test\stdout.log",
            StandardErrorLogPath = @"C:\runs\agy-test\stderr.log",
            StandardOutputBytes = standardOutput.Length,
            StandardErrorBytes = 0,
            StandardOutputHead = standardOutput,
            StandardOutputTail = standardOutput,
            StandardErrorHead = string.Empty,
            StandardErrorTail = string.Empty,
            OutputOverflowed = false
        };
    }

    private sealed class StubCliExecutableLocator : ICliExecutableLocator
    {
        public string? ResolvedPath { get; set; } = @"C:\tools\agy.exe";

        public List<string> RequestedNames { get; } = new();

        public Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default)
        {
            RequestedNames.Add(executableName);

            return Task.FromResult(ResolvedPath);
        }
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        private readonly string _standardOutput;
        private readonly int _exitCode;
        private readonly ProcessTerminationReason _terminationReason;
        private readonly bool _hangUntilCancelled;

        public StubProcessSupervisor(
            string standardOutput,
            int exitCode,
            ProcessTerminationReason terminationReason,
            bool hangUntilCancelled)
        {
            _standardOutput = standardOutput;
            _exitCode = exitCode;
            _terminationReason = terminationReason;
            _hangUntilCancelled = hangUntilCancelled;
        }

        public ProcessStartSpecification? Captured { get; private set; }

        public CancellationToken CapturedToken { get; private set; }

        public int CallCount { get; private set; }

        public async Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Captured = specification;
            CapturedToken = cancellationToken;

            if (_hangUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (_standardOutput.Length > 0)
            {
                outputProgress?.Report(new ProcessOutputEvent(
                    ProcessStreamKind.StdOut,
                    _standardOutput,
                    DateTimeOffset.UtcNow,
                    _standardOutput.Length));
            }

            cancellationToken.ThrowIfCancellationRequested();

            return CreateResult(specification.ExecutionId, _standardOutput, _exitCode, _terminationReason);
        }
    }
}

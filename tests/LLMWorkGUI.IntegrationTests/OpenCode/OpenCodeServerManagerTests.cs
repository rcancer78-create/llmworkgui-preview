using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.IntegrationTests.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed partial class OpenCodeServerManagerTests
{
    private const int FakePort = 54321;

    [Fact]
    public async Task StopServerAsync_HungSupervisorIsBoundedAndRetainsSharedCleanup()
    {
        var supervisor = new RecordingProcessSupervisor("Listening on http://127.0.0.1:5001")
            { CompleteOnCancellation = false };
        var requests = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath = typeof(OpenCodeServerManagerTests).Assembly.Location,
            DisposeTimeout = TimeSpan.FromMilliseconds(100)
        }), supervisor, new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")), http,
            new FixedProcessIdResolver(123));
        var instance = await manager.StartServerAsync();
        var ownedInstance = Assert.IsType<OpenCodeServerInstance>(instance);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            var first = manager.StopServerAsync(instance, cancelled.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(first.IsFaulted); // The manager's deadline, not the outer test deadline.
            // Readiness is retired immediately; the exact execution and shared cleanup remain owned.
            Assert.False(instance.IsAlive);
            Assert.False(instance.IsTerminationConfirmed);
            Assert.False(ownedInstance.ExecutionTask.IsCompleted);
            Assert.Equal(1, manager.OwnedInstanceCount);
            var second = manager.StopServerAsync(instance);
            await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(second.IsFaulted);
            Assert.True(supervisor.WasCancelled);
            Assert.Equal(1, requests);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartServerAsync());
            Assert.False(ownedInstance.ExecutionTask.IsCompleted);
            Assert.False(instance.IsTerminationConfirmed);
            Assert.Equal(1, manager.OwnedInstanceCount);
        }
        finally { supervisor.Complete(); }
        await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(instance.IsAlive);
        Assert.True(ownedInstance.ExecutionTask.IsCompletedSuccessfully);
        Assert.True(instance.IsTerminationConfirmed);
        Assert.Equal(0, manager.OwnedInstanceCount);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task StartServerAsync_WithoutOwnedLaunch_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-7c1e";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var supervisor = new RecordingProcessSupervisor("Listening on http://127.0.0.1:5001");
            using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
            {
                CustomExecutablePath = typeof(OpenCodeServerManagerTests).Assembly.Location,
                DisposeTimeout = TimeSpan.FromMilliseconds(100)
            }), supervisor, new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")), http,
                new FixedProcessIdResolver(123));

            var instance = await manager.StartServerAsync();
            try
            {
                var specification = supervisor.Specification;
                Assert.NotNull(specification);
                Assert.False(specification!.InheritEnvironment);
                Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
                Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
                Assert.Equal("true", specification.EnvironmentVariables["OPENCODE_DISABLE_PROJECT_CONFIG"]);
                Assert.Equal("true", specification.EnvironmentVariables["OPENCODE_DISABLE_DEFAULT_PLUGINS"]);
                var home = specification.EnvironmentVariables["USERPROFILE"];
                var realProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Assert.False(string.Equals(home, realProfile, StringComparison.OrdinalIgnoreCase));
                Assert.True(Directory.Exists(home));
                Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
                using var config = JsonDocument.Parse(specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"]);
                var plugins = config.RootElement.GetProperty("plugin");
                Assert.Equal(JsonValueKind.Array, plugins.ValueKind);
                Assert.Equal(0, plugins.GetArrayLength());
                Assert.DoesNotContain("codex", specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"], StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("agy", specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"], StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("antigravity", specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"], StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                supervisor.Complete();
                await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task StartServerAsync_WithManagedCredential_PublishesPasswordAndDisposeUsesIt()
    {
        var credential = new OpenCodeManagedServerCredential();
        string? authorization = null;
        var supervisor = new RecordingProcessSupervisor("Listening on http://127.0.0.1:5001");
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath = typeof(OpenCodeServerManagerTests).Assembly.Location,
            DisposeTimeout = TimeSpan.FromMilliseconds(100)
        }), supervisor, new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")), http,
            new FixedProcessIdResolver(123), managedCredential: credential);

        var instance = await manager.StartServerAsync();
        var specification = supervisor.Specification;
        Assert.NotNull(specification);
        var password = specification!.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"];
        Assert.Equal(OpenCodeManagedServerCredential.Username, specification.EnvironmentVariables["OPENCODE_SERVER_USERNAME"]);
        Assert.False(string.IsNullOrWhiteSpace(password));
        try
        {
            supervisor.Complete();
            await manager.StopServerAsync(instance).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            supervisor.Complete();
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            OpenCodeManagedServerCredential.Username + ":" + password));
        Assert.Equal(expected, authorization);
    }

    [Fact]
    public async Task Profiles_DoNotShareAManagedProcess()
    {
        var supervisor = new DistinctPortSupervisor();
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var manager = new OpenCodeServerManager(Options.Create(new OpenCodeServerOptions
        {
            CustomExecutablePath = typeof(OpenCodeServerManagerTests).Assembly.Location,
            DisposeTimeout = TimeSpan.FromMilliseconds(200)
        }), supervisor, new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("unused")), http,
            new FixedProcessIdResolver(123), managedCredential: new OpenCodeManagedServerCredential());
        await using var connection = new OpenCodeServerConnection(manager);

        await Assert.ThrowsAsync<ArgumentException>(() => connection.GetBaseUrlAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => connection.GetBaseUrlAsync(string.Empty));
        Assert.Empty(supervisor.Specifications);

        var profileA = await connection.GetBaseUrlAsync("profile-a");
        var profileAAgain = await connection.GetBaseUrlAsync("profile-a");
        var profileB = await connection.GetBaseUrlAsync("profile-b");
        var unnamed = await connection.GetBaseUrlAsync();

        Assert.Equal(profileA, profileAAgain);
        Assert.NotEqual(profileA, profileB);
        Assert.NotEqual(profileA, unnamed);
        Assert.NotEqual(profileB, unnamed);
        Assert.Equal(3, supervisor.Specifications.Count);

        var homes = supervisor.Specifications.Select(specification => specification.EnvironmentVariables["USERPROFILE"]).ToArray();
        Assert.Equal(3, homes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var passwords = supervisor.Specifications.Select(specification => specification.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"]).ToArray();
        Assert.Equal(3, passwords.Distinct(StringComparer.Ordinal).Count());
        Assert.All(supervisor.Specifications, specification =>
        {
            Assert.False(specification.InheritEnvironment);
            using var config = JsonDocument.Parse(specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"]);
            Assert.Equal(0, config.RootElement.GetProperty("plugin").GetArrayLength());
            Assert.DoesNotContain("codex", specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agy", specification.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"], StringComparison.OrdinalIgnoreCase);
        });

        supervisor.CompleteAll();
    }

    [Fact]
    public async Task StartServerAsync_ExtractsAssignedPort_AndStopsWithDispose()
    {
        using var fakeServer = new OpenCodeFakeServer($"Listening on http://127.0.0.1:{FakePort}");
        using var dataDirectory = new TestDirectory();
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = CreateManager(
            fakeServer.ScriptPath,
            dataDirectory.Root,
            httpClient,
            new FixedProcessIdResolver(4242),
            TimeSpan.FromSeconds(20));

        var instance = await manager.StartServerAsync();

        Assert.Equal(FakePort, instance.AssignedPort);
        Assert.Equal("127.0.0.1", instance.BaseUrl.Host);
        Assert.Equal(FakePort, instance.BaseUrl.Port);
        Assert.Equal(4242, instance.ProcessId);
        Assert.True(instance.ProcessGeneration > 0);
        Assert.True(instance.IsAlive);
        Assert.StartsWith("opencode-server-", instance.InstanceId, StringComparison.Ordinal);
        Assert.True(instance.StartedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));

        var serverProcessId = await fakeServer.GetServerProcessIdAsync();
        var helperProcessId = await fakeServer.GetHelperProcessIdAsync();

        await manager.StopServerAsync(instance);

        Assert.False(instance.IsAlive);
        Assert.Contains(
            handler.Requests,
            request => request.Method == "POST" && request.Path == "/instance/dispose");
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            serverProcessId,
            TimeSpan.FromSeconds(20)));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(helperProcessId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StartServerAsync_WhenPortIsNeverReported_TimesOutAndTerminatesProcess()
    {
        using var fakeServer = new OpenCodeFakeServer(startupLine: null);
        using var dataDirectory = new TestDirectory();
        using var httpClient = CreateHttpClient();
        var manager = CreateManager(
            fakeServer.ScriptPath,
            dataDirectory.Root,
            httpClient,
            new FixedProcessIdResolver(null),
            TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAsync<OpenCodeServerStartupException>(
            () => manager.StartServerAsync());

        Assert.Contains("did not report a listening port", exception.Message, StringComparison.OrdinalIgnoreCase);

        var serverProcessId = await fakeServer.GetServerProcessIdAsync();
        var helperProcessId = await fakeServer.GetHelperProcessIdAsync();
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            serverProcessId,
            TimeSpan.FromSeconds(20)));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(helperProcessId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StartServerAsync_WhenProcessExitsBeforeReportingPort_ThrowsWithExitCode()
    {
        using var fakeServer = new OpenCodeFakeServer(startupLine: null, exitCode: 2);
        using var dataDirectory = new TestDirectory();
        using var httpClient = CreateHttpClient();
        var manager = CreateManager(
            fakeServer.ScriptPath,
            dataDirectory.Root,
            httpClient,
            new FixedProcessIdResolver(null),
            TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<OpenCodeServerStartupException>(
            () => manager.StartServerAsync());

        Assert.Contains("exit code 2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartServerAsync_WhenCliIsNotDiscovered_ThrowsStartupException()
    {
        using var dataDirectory = new TestDirectory();
        using var httpClient = CreateHttpClient();
        var manager = new OpenCodeServerManager(
            Options.Create(new OpenCodeServerOptions { StartupTimeout = TimeSpan.FromSeconds(1) }),
            CreateSupervisor(dataDirectory.Root),
            new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("opencode was not found.")),
            httpClient,
            new FixedProcessIdResolver(null),
            logger: NullLogger<OpenCodeServerManager>.Instance);

        var exception = await Assert.ThrowsAsync<OpenCodeServerStartupException>(
            () => manager.StartServerAsync());

        Assert.Contains("opencode was not found.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartServerAsync_WhenVersionIsUnsupported_ThrowsStartupException()
    {
        using var dataDirectory = new TestDirectory();
        using var httpClient = CreateHttpClient();
        var manager = new OpenCodeServerManager(
            Options.Create(new OpenCodeServerOptions { StartupTimeout = TimeSpan.FromSeconds(1) }),
            CreateSupervisor(dataDirectory.Root),
            new StubDiscoveryService(
                OpenCodeDiscoveryResult.Unsupported(@"C:\fake\opencode.exe", "1.17.9", "below baseline")),
            httpClient,
            new FixedProcessIdResolver(null),
            logger: NullLogger<OpenCodeServerManager>.Instance);

        var exception = await Assert.ThrowsAsync<OpenCodeServerStartupException>(
            () => manager.StartServerAsync());

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartServerAsync_UsesServeArgumentsAndClosedStdin()
    {
        var supervisor = new RecordingProcessSupervisor("Listening on http://127.0.0.1:5001");
        using var dataDirectory = new TestDirectory();
        var executablePath = Path.Combine(dataDirectory.Root, "opencode.exe");
        File.WriteAllText(executablePath, string.Empty);
        using var httpClient = CreateHttpClient();
        var manager = new OpenCodeServerManager(
            Options.Create(new OpenCodeServerOptions { CustomExecutablePath = executablePath }),
            supervisor,
            new StubDiscoveryService(OpenCodeDiscoveryResult.NotInstalled("not used")),
            httpClient,
            new FixedProcessIdResolver(7),
            logger: NullLogger<OpenCodeServerManager>.Instance);

        var instance = await manager.StartServerAsync();

        Assert.NotNull(supervisor.Specification);
        Assert.Equal(executablePath, supervisor.Specification!.FileName);
        Assert.Equal(
            new[] { "serve", "--port", "0", "--hostname", "127.0.0.1" },
            supervisor.Specification.Arguments);
        Assert.Equal(ProcessStdinPolicy.Closed, supervisor.Specification.StdinPolicy);
        Assert.Equal(5001, instance.AssignedPort);

        await manager.StopServerAsync(instance);

        Assert.True(supervisor.WasCancelled);
    }

    [Fact]
    public async Task StopServerAsync_WhenDisposeRequestFails_StillTerminatesProcess()
    {
        using var fakeServer = new OpenCodeFakeServer($"Listening on http://127.0.0.1:{FakePort}");
        using var dataDirectory = new TestDirectory();
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = CreateManager(
            fakeServer.ScriptPath,
            dataDirectory.Root,
            httpClient,
            new FixedProcessIdResolver(11),
            TimeSpan.FromSeconds(20));

        var instance = await manager.StartServerAsync();
        var serverProcessId = await fakeServer.GetServerProcessIdAsync();
        var helperProcessId = await fakeServer.GetHelperProcessIdAsync();

        await manager.StopServerAsync(instance);

        Assert.False(instance.IsAlive);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(
            serverProcessId,
            TimeSpan.FromSeconds(20)));
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(helperProcessId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopServerAsync_WhenCalledTwice_IsIdempotent()
    {
        using var fakeServer = new OpenCodeFakeServer($"Listening on http://127.0.0.1:{FakePort}");
        using var dataDirectory = new TestDirectory();
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = CreateManager(
            fakeServer.ScriptPath,
            dataDirectory.Root,
            httpClient,
            new FixedProcessIdResolver(12),
            TimeSpan.FromSeconds(20));

        var instance = await manager.StartServerAsync();

        var helperProcessId = await fakeServer.GetHelperProcessIdAsync();
        await manager.StopServerAsync(instance);
        await manager.StopServerAsync(instance);
        Assert.True(await FakeProcessHarness.WaitForProcessExitAsync(helperProcessId, TimeSpan.FromSeconds(5)));

        Assert.False(instance.IsAlive);
        Assert.Single(handler.Requests, request => request.Method == "POST");
    }

    private static OpenCodeServerManager CreateManager(
        string executablePath,
        string appDataDirectory,
        HttpClient httpClient,
        IProcessIdResolver processIdResolver,
        TimeSpan startupTimeout)
    {
        var options = new OpenCodeServerOptions
        {
            CustomExecutablePath = executablePath,
            StartupTimeout = startupTimeout,
            DisposeTimeout = TimeSpan.FromSeconds(5)
        };

        return new OpenCodeServerManager(
            Options.Create(options),
            CreateSupervisor(appDataDirectory),
            new StubDiscoveryService(
                OpenCodeDiscoveryResult.NotInstalled("discovery is not expected with a custom executable path.")),
            httpClient,
            processIdResolver,
            logger: NullLogger<OpenCodeServerManager>.Instance);
    }

    private static ProcessSupervisor CreateSupervisor(string appDataDirectory)
    {
        return new ProcessSupervisor(
            Options.Create(new ProcessSupervisorOptions
            {
                GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500)
            }),
            new StorageOptions { AppDataDirectory = appDataDirectory });
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed class FixedProcessIdResolver : IProcessIdResolver
    {
        private readonly int? _processId;

        public FixedProcessIdResolver(int? processId)
        {
            _processId = processId;
        }

        public int? ResolveChildProcessId(string executablePath, DateTimeOffset startedAfterUtc)
        {
            return _processId;
        }
    }

    private sealed class StubDiscoveryService : IOpenCodeDiscoveryService
    {
        private readonly OpenCodeDiscoveryResult _result;

        public StubDiscoveryService(OpenCodeDiscoveryResult result)
        {
            _result = result;
        }

        public Task<OpenCodeDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingProcessSupervisor : IProcessSupervisor
    {
        private readonly string _startupLine;
        private CancellationTokenSource? _linkedLifetime;
        private Action? _complete;
        public bool CompleteOnCancellation { get; set; } = true;
        public void Complete() => _complete?.Invoke();

        public RecordingProcessSupervisor(string startupLine)
        {
            _startupLine = startupLine;
        }

        public ProcessStartSpecification? Specification { get; private set; }

        public bool WasCancelled => _linkedLifetime?.IsCancellationRequested ?? false;

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            Specification = specification;
            _linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var completion = new TaskCompletionSource<ProcessExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            outputProgress?.Report(new ProcessOutputEvent(
                ProcessStreamKind.StdOut,
                _startupLine,
                DateTimeOffset.UtcNow,
                _startupLine.Length));

            _complete = () =>
            {
                var now = DateTimeOffset.UtcNow;

                completion.TrySetResult(new ProcessExecutionResult
                {
                    ExecutionId = specification.ExecutionId,
                    ProcessId = 123,
                    TerminationReason = ProcessTerminationReason.UserCancelled,
                    ExitCode = null,
                    StartedAtUtc = now,
                    ExitedAtUtc = now,
                    RunDirectory = string.Empty,
                    StandardOutputLogPath = string.Empty,
                    StandardErrorLogPath = string.Empty,
                    StandardOutputBytes = 0,
                    StandardErrorBytes = 0,
                    StandardOutputHead = string.Empty,
                    StandardOutputTail = string.Empty,
                    StandardErrorHead = string.Empty,
                    StandardErrorTail = string.Empty,
                    OutputOverflowed = false
                });
            };
            _linkedLifetime.Token.Register(() => { if (CompleteOnCancellation) Complete(); });

            return completion.Task;
        }
    }

    private sealed class DistinctPortSupervisor : IProcessSupervisor
    {
        private int _nextPort = 14011;
        private readonly List<Action> _complete = new();
        public List<ProcessStartSpecification> Specifications { get; } = new();

        public void CompleteAll()
        {
            foreach (var complete in _complete) complete();
        }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            Specifications.Add(specification);
            var port = _nextPort++;
            var line = "Listening on http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            outputProgress?.Report(new ProcessOutputEvent(ProcessStreamKind.StdOut, line, DateTimeOffset.UtcNow, line.Length));
            _complete.Add(() =>
            {
                var now = DateTimeOffset.UtcNow;
                completion.TrySetResult(new ProcessExecutionResult
                {
                    ExecutionId = specification.ExecutionId,
                    ProcessId = port,
                    TerminationReason = ProcessTerminationReason.UserCancelled,
                    ExitCode = null,
                    StartedAtUtc = now,
                    ExitedAtUtc = now,
                    RunDirectory = string.Empty,
                    StandardOutputLogPath = string.Empty,
                    StandardErrorLogPath = string.Empty,
                    StandardOutputBytes = 0,
                    StandardErrorBytes = 0,
                    StandardOutputHead = string.Empty,
                    StandardOutputTail = string.Empty,
                    StandardErrorHead = string.Empty,
                    StandardErrorTail = string.Empty,
                    OutputOverflowed = false
                });
            });
            cancellationToken.Register(CompleteAll);
            return completion.Task;
        }
    }
}

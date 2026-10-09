using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyServerManagerTests
{
    private const string FoundExecutablePath = @"C:\tools\star-cliproxy.exe";

    [Fact]
    public void IsAvailable_TrueWhenResolverFindsGateway()
    {
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver());

        Assert.True(manager.IsAvailable);
        Assert.Null(manager.AvailabilityBlocker);
    }

    [Fact]
    public async Task IsAvailable_FalseWithoutGateway_ReportsDegradedBlockerWithProjectUrl()
    {
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(
            dataDirectory.Root,
            new FakeExecutableResolver
            {
                Resolution = StarCliProxyExecutableResolution.NotFound("not found")
            });

        Assert.False(manager.IsAvailable);
        Assert.Contains("not found", manager.AvailabilityBlocker);
        Assert.Contains(StarCliProxyPolicy.ProjectUrl, StarCliProxyPolicy.NotInstalledBlocker);

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest { ExecutionId = "exec-1" }));

        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public void IsAvailable_FalseWhenCustomExecutablePathMissing()
    {
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(
            dataDirectory.Root,
            new FakeExecutableResolver(),
            options: new StarCliProxyOptions
            {
                CustomExecutablePath = @"C:\missing\star-cliproxy.exe"
            });

        Assert.False(manager.IsAvailable);
        Assert.Contains(@"C:\missing\star-cliproxy.exe", manager.AvailabilityBlocker);
        Assert.Contains(StarCliProxyPolicy.ProjectUrl, manager.AvailabilityBlocker);
    }

    [Fact]
    public async Task StartServer_LaunchesSupervisedProcessWithConfigAndIsolatedCodexHome()
    {
        using var dataDirectory = new TestDirectory();
        using var codexHome = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var client = new FakeStarCliProxyClient();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor, client);

        var globalCodexHomeBefore = Environment.GetEnvironmentVariable("CODEX_HOME");

        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
        {
            ExecutionId = "exec-codex",
            CodexHomePath = codexHome.Root,
            EnabledProviderIds = ["codex"]
        });

        // The CODEX_HOME overlay exists only inside the owned process environment.
        Assert.Equal(
            globalCodexHomeBefore,
            Environment.GetEnvironmentVariable("CODEX_HOME"));

        var specification = Assert.IsType<ProcessStartSpecification>(supervisor.LastSpecification);
        Assert.Equal("exec-codex", specification.ExecutionId);
        Assert.Equal(FoundExecutablePath, specification.FileName);
        Assert.Contains(instance.ConfigFilePath, specification.Arguments);
        Assert.Equal(codexHome.Root, specification.EnvironmentVariables["CODEX_HOME"]);
        Assert.StartsWith(StarCliProxyServerManager.ProxyApiKeyPrefix, specification.EnvironmentVariables["PROXY_API_KEY"]);
        Assert.False(string.IsNullOrWhiteSpace(specification.EnvironmentVariables["ADMIN_TOKEN"]));
        var joinedArguments = string.Join(" ", specification.Arguments);
        Assert.False(
            joinedArguments.Contains(specification.EnvironmentVariables["PROXY_API_KEY"], StringComparison.Ordinal),
            "Proxy API key detected in process arguments.");
        Assert.False(
            joinedArguments.Contains(specification.EnvironmentVariables["ADMIN_TOKEN"], StringComparison.Ordinal),
            "Admin token detected in process arguments.");

        // The generated configuration lives in the app-owned run directory outside the project root.
        var expectedRunDirectory = Path.Combine(dataDirectory.Root, "runs", "exec-codex");
        Assert.Equal(Path.Combine(expectedRunDirectory, "config.yaml"), instance.ConfigFilePath);
        Assert.StartsWith(expectedRunDirectory, instance.ConfigFilePath);
        Assert.DoesNotContain(
            Path.GetFullPath(Directory.GetCurrentDirectory()),
            instance.ConfigFilePath);
        Assert.True(File.Exists(instance.ConfigFilePath));

        var config = await File.ReadAllTextAsync(instance.ConfigFilePath);
        Assert.Contains("host: \"127.0.0.1\"", config);
        Assert.Contains($"port: {instance.AssignedPort}", config);
        Assert.Contains("\"codex\":", config);
        Assert.DoesNotContain("agy:", config);
        Assert.Contains("${PROXY_API_KEY}", config);
        Assert.False(
            config.Contains(specification.EnvironmentVariables["PROXY_API_KEY"], StringComparison.Ordinal),
            "Proxy API key detected in config.yaml.");
        Assert.False(
            config.Contains(specification.EnvironmentVariables["ADMIN_TOKEN"], StringComparison.Ordinal),
            "Admin token detected in config.yaml.");
        Assert.False(File.Exists(Path.Combine(expectedRunDirectory, ".env")));

        Assert.True(instance.IsAlive);
        Assert.Equal(codexHome.Root, instance.CodexHomePath);
        Assert.True(instance.AssignedPort > 0);
        Assert.Equal($"http://127.0.0.1:{instance.AssignedPort}/", instance.BaseUrl.ToString());
        Assert.True(client.HealthCallCount >= 1);
    }

    [Fact]
    public async Task StartServer_StaleEnvironmentFile_DeletesStaleFileAndLeavesNoSecretsOnDisk()
    {
        const string Sentinel = "stale-sentinel-secret-0d9f4b1e";

        using var dataDirectory = new TestDirectory();

        var runDirectory = Path.Combine(dataDirectory.Root, "runs", "exec-stale-env");
        Directory.CreateDirectory(runDirectory);

        var environmentPath = Path.Combine(runDirectory, ".env");
        await File.WriteAllTextAsync(
            environmentPath,
            $"ADMIN_TOKEN={Sentinel}{Environment.NewLine}PROXY_API_KEY={Sentinel}{Environment.NewLine}");

        Assert.True(File.Exists(environmentPath));

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
        {
            ExecutionId = "exec-stale-env"
        });

        Assert.False(File.Exists(environmentPath));

        var specification = Assert.IsType<ProcessStartSpecification>(supervisor.LastSpecification);
        var adminToken = specification.EnvironmentVariables["ADMIN_TOKEN"];

        var files = Directory.GetFiles(runDirectory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);

            Assert.False(content.Contains(Sentinel, StringComparison.Ordinal), $"Sentinel detected in {file}");
            Assert.False(content.Contains(instance.ApiKey, StringComparison.Ordinal), $"API key detected in {file}");
            Assert.False(content.Contains(adminToken, StringComparison.Ordinal), $"Admin token detected in {file}");
        }

        var configPath = Path.Combine(runDirectory, "config.yaml");
        Assert.Contains(configPath, files);

        var config = await File.ReadAllTextAsync(configPath);
        Assert.Contains("${PROXY_API_KEY}", config);
        Assert.Contains("${ADMIN_TOKEN}", config);
    }

    [Fact]
    public async Task StartServer_UndeletableStaleEnvironmentFile_FailsClosedBeforeConfigWrite()
    {
        using var dataDirectory = new TestDirectory();

        var runDirectory = Path.Combine(dataDirectory.Root, "runs", "exec-locked-env");
        Directory.CreateDirectory(runDirectory);

        var environmentPath = Path.Combine(runDirectory, ".env");
        await File.WriteAllTextAsync(
            environmentPath,
            $"ADMIN_TOKEN=stale{Environment.NewLine}PROXY_API_KEY=stale{Environment.NewLine}");

        using var lockStream = File.Open(
            environmentPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        await Assert.ThrowsAsync<IOException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest
            {
                ExecutionId = "exec-locked-env"
            }));

        Assert.Null(supervisor.LastSpecification);

        var configPath = Path.Combine(runDirectory, "config.yaml");
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public async Task StartServer_DoesNotInheritTheParentEnvironment()
    {
        const string sentinelName = "LLMWORKGUI_PARENT_SENTINEL";
        const string sentinelValue = "parent-only-proxy";
        var previous = Environment.GetEnvironmentVariable(sentinelName);
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            using var dataDirectory = new TestDirectory();
            var supervisor = new FakeProcessSupervisor();
            var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

            await manager.StartServerAsync(new StarCliProxyServerStartRequest
            {
                ExecutionId = "exec-no-inherit"
            });

            var specification = supervisor.LastSpecification;
            Assert.NotNull(specification);
            Assert.False(specification!.InheritEnvironment);
            Assert.False(specification.EnvironmentVariables.ContainsKey(sentinelName));
            Assert.DoesNotContain(sentinelValue, specification.EnvironmentVariables.Values);
            Assert.Contains(Environment.SystemDirectory, specification.EnvironmentVariables["PATH"], StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(specification.EnvironmentVariables["PROXY_API_KEY"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, previous);
        }
    }

    [Fact]
    public async Task StartServer_WithoutCodexHome_DoesNotSetCodexHomeEnvironment()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
        {
            ExecutionId = "exec-agy"
        });

        Assert.NotNull(supervisor.LastSpecification);
        Assert.False(supervisor.LastSpecification!.EnvironmentVariables.ContainsKey("CODEX_HOME"));
        Assert.Null(instance.CodexHomePath);

        var config = await File.ReadAllTextAsync(instance.ConfigFilePath);
        Assert.Contains("\"agy\":", config);
    }

    [Fact]
    public async Task StartServer_RelativeCodexHome_FailsClosedBeforeLaunch()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest
            {
                ExecutionId = "exec-relative",
                CodexHomePath = @"relative\codex-home"
            }));

        Assert.Contains("absolute", exception.Message);
        Assert.Null(supervisor.LastSpecification);
    }

    [Fact]
    public async Task StartServer_MissingCodexHomeDirectory_FailsClosedBeforeLaunch()
    {
        using var dataDirectory = new TestDirectory();
        var missing = Path.Combine(dataDirectory.Root, "missing-home");

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest
            {
                ExecutionId = "exec-missing-home",
                CodexHomePath = missing
            }));

        Assert.Contains("does not exist", exception.Message);
        Assert.Null(supervisor.LastSpecification);
    }

    [Fact]
    public async Task StartServer_InvalidProviderId_FailsClosed()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest
            {
                ExecutionId = "exec-invalid-provider",
                EnabledProviderIds = ["codex: inject"]
            }));

        Assert.Contains("invalid", exception.Message);
        Assert.Null(supervisor.LastSpecification);
    }

    [Fact]
    public async Task StartServer_ProcessExitsBeforeHealthy_ThrowsStartupException()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor { CompleteImmediatelyWithExitCode = 7 };
        var client = new FakeStarCliProxyClient { AlwaysHealthy = false };
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor, client);

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest { ExecutionId = "exec-exit" }));

        Assert.Contains("exited before becoming healthy", exception.Message);
        Assert.Contains("7", exception.Message);
    }

    [Fact]
    public async Task StartServer_HealthTimeout_ThrowsStartupException()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var client = new FakeStarCliProxyClient { AlwaysHealthy = false };

        var manager = CreateManager(
            dataDirectory.Root,
            new FakeExecutableResolver(),
            supervisor,
            client,
            options: new StarCliProxyOptions { StartupTimeout = TimeSpan.FromMilliseconds(250) });

        var exception = await Assert.ThrowsAsync<StarCliProxyStartupException>(() =>
            manager.StartServerAsync(new StarCliProxyServerStartRequest { ExecutionId = "exec-timeout" }));

        Assert.Contains("did not become healthy", exception.Message);
    }

    [Fact]
    public async Task StopServer_CancelsOwnedProcessTree()
    {
        using var dataDirectory = new TestDirectory();

        var supervisor = new FakeProcessSupervisor();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), supervisor);

        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
        {
            ExecutionId = "exec-stop"
        });

        Assert.True(instance.IsAlive);

        await manager.StopServerAsync(instance);

        Assert.True(supervisor.CancellationObserved);
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public async Task StopServer_RejectsForeignInstance()
    {
        using var dataDirectory = new TestDirectory();

        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver());

        await Assert.ThrowsAsync<ArgumentException>(() => manager.StopServerAsync(new ForeignInstance()));
    }

    [Fact]
    public async Task CheckHealth_DelegatesToClientAndRejectsForeignInstance()
    {
        using var dataDirectory = new TestDirectory();

        var client = new FakeStarCliProxyClient();
        var manager = CreateManager(dataDirectory.Root, new FakeExecutableResolver(), client: client);

        var instance = await manager.StartServerAsync(new StarCliProxyServerStartRequest
        {
            ExecutionId = "exec-health"
        });

        var health = await manager.CheckHealthAsync(instance);

        Assert.True(health.IsHealthy);
        Assert.True(client.HealthCallCount >= 2);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.CheckHealthAsync(new ForeignInstance()));
    }

    [Fact]
    public void Constructor_RejectsNonLoopbackHostname()
    {
        using var dataDirectory = new TestDirectory();

        Assert.Throws<ArgumentException>(() => CreateManager(
            dataDirectory.Root,
            new FakeExecutableResolver(),
            options: new StarCliProxyOptions { Hostname = "0.0.0.0" }));
    }

    private static StarCliProxyServerManager CreateManager(
        string dataRoot,
        FakeExecutableResolver resolver,
        FakeProcessSupervisor? supervisor = null,
        FakeStarCliProxyClient? client = null,
        StarCliProxyOptions? options = null)
    {
        return new StarCliProxyServerManager(
            Options.Create(options ?? new StarCliProxyOptions { StartupTimeout = TimeSpan.FromSeconds(5) }),
            resolver,
            supervisor ?? new FakeProcessSupervisor(),
            client ?? new FakeStarCliProxyClient(),
            new LLMWorkGUI.Application.Configuration.StorageOptions { AppDataDirectory = dataRoot });
    }

    private sealed class FakeExecutableResolver : IStarCliProxyExecutableResolver
    {
        public StarCliProxyExecutableResolution Resolution { get; set; } =
            StarCliProxyExecutableResolution.Found(FoundExecutablePath);

        public StarCliProxyExecutableResolution Resolve() => Resolution;
    }

    private sealed class FakeProcessSupervisor : IProcessSupervisor
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? CompleteImmediatelyWithExitCode { get; set; }

        public ProcessStartSpecification? LastSpecification { get; private set; }

        public bool CancellationObserved { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            LastSpecification = specification;

            if (CompleteImmediatelyWithExitCode is { } exitCode)
            {
                _completion.TrySetResult(CreateResult(specification.ExecutionId, exitCode));
            }

            cancellationToken.Register(() =>
            {
                CancellationObserved = true;
                _completion.TrySetCanceled(cancellationToken);
            });

            return _completion.Task;
        }

        private static ProcessExecutionResult CreateResult(string executionId, int exitCode)
        {
            return new ProcessExecutionResult
            {
                ExecutionId = executionId,
                TerminationReason = ProcessTerminationReason.None,
                ExitCode = exitCode,
                StartedAtUtc = DateTimeOffset.UtcNow,
                ExitedAtUtc = DateTimeOffset.UtcNow,
                RunDirectory = "run",
                StandardOutputLogPath = "stdout.log",
                StandardErrorLogPath = "stderr.log",
                StandardOutputBytes = 0,
                StandardErrorBytes = 0,
                StandardOutputHead = string.Empty,
                StandardOutputTail = string.Empty,
                StandardErrorHead = string.Empty,
                StandardErrorTail = string.Empty,
                OutputOverflowed = false
            };
        }
    }

    private sealed class FakeStarCliProxyClient : IStarCliProxyClient
    {
        public bool AlwaysHealthy { get; set; } = true;

        public int HealthCallCount { get; private set; }

        public Task<StarCliProxyHealthStatus> CheckHealthAsync(
            StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            HealthCallCount++;

            return Task.FromResult(AlwaysHealthy
                ? StarCliProxyHealthStatus.Healthy(2, DateTimeOffset.UtcNow)
                : StarCliProxyHealthStatus.Unhealthy("not ready", DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<StarCliProxyModelInfo>> ListModelsAsync(
            StarCliProxyEndpoint endpoint,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StarCliProxyStreamEvent> StreamChatCompletionAsync(
            StarCliProxyEndpoint endpoint,
            StarCliProxyChatRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ForeignInstance : IStarCliProxyServerInstance
    {
        public string InstanceId => "foreign";

        public int? ProcessId => null;

        public int AssignedPort => 1;

        public Uri BaseUrl => new("http://127.0.0.1:1/");

        public string? CodexHomePath => null;

        public string ConfigFilePath => "config.yaml";

        public string ApiKey => "sk-proxy-foreign";

        public bool IsAlive => false;

        public DateTimeOffset StartedAtUtc => DateTimeOffset.UtcNow;
    }
}

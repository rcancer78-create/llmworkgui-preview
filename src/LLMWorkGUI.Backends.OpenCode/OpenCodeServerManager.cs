using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed class OpenCodeServerManager : IOpenCodeServerManager
{
    private readonly OpenCodeServerOptions _options;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly IOpenCodeDiscoveryService _discoveryService;
    private readonly HttpClient _httpClient;
    private readonly IProcessIdResolver _processIdResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenCodeServerManager> _logger;
    private readonly ConcurrentDictionary<string, OpenCodeServerInstance> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _pendingStartupCleanup = new(StringComparer.Ordinal);
    private readonly object _ownershipGate = new();
    private bool _starting;
    private readonly OpenCodeOwnedServerLaunch? _ownedLaunch;
    private readonly OpenCodeManagedServerCredential? _managedCredential;
    private readonly Dictionary<string, string> _passwordsByInstance = new(StringComparer.Ordinal);
    private bool _ownedLaunchAttempted;

    internal int PendingStartupCleanupCount => _pendingStartupCleanup.Count;
    internal int OwnedInstanceCount => _instances.Count;

    internal async Task WaitForRetainedStartupCleanupAsync()
    {
        // A failed start can leave an owner without handing an instance to the connection.
        // Observe those exact retained operations; never launch another cleanup or native start.
        await Task.WhenAll(_pendingStartupCleanup.Values.ToArray()).ConfigureAwait(false);
        if (!_pendingStartupCleanup.IsEmpty) throw TerminationUnconfirmed();
    }

    public OpenCodeServerManager(
        IOptions<OpenCodeServerOptions> options,
        IProcessSupervisor processSupervisor,
        IOpenCodeDiscoveryService discoveryService,
        HttpClient httpClient,
        IProcessIdResolver? processIdResolver = null,
        TimeProvider? timeProvider = null,
        ILogger<OpenCodeServerManager>? logger = null,
        OpenCodeOwnedServerLaunch? ownedLaunch = null,
        OpenCodeManagedServerCredential? managedCredential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(discoveryService);
        ArgumentNullException.ThrowIfNull(httpClient);

        _options = options.Value;
        _options.Validate();
        if (ownedLaunch is not null && (_options.Port != 0 || _options.CustomExecutablePath is null))
            throw new ArgumentException("An owned launch requires an explicit executable and an OS-assigned port.", nameof(ownedLaunch));
        _ownedLaunch = ownedLaunch;
        _managedCredential = managedCredential;
        _processSupervisor = processSupervisor;
        _discoveryService = discoveryService;
        _httpClient = httpClient;
        _processIdResolver = processIdResolver ?? WindowsChildProcessIdResolver.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OpenCodeServerManager>.Instance;
    }

    public async Task<IOpenCodeServerInstance> StartServerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_ownershipGate)
        {
            if (_ownedLaunchAttempted || _starting || !_pendingStartupCleanup.IsEmpty
                || _instances.Values.Any(instance => instance.StopRequested || !instance.IsAlive))
                throw new InvalidOperationException("Запуск OpenCode отклонён: предыдущий запуск или завершение принадлежащего приложения дерева ещё не подтверждены. Новый процесс не запускался.");
            _starting = true;
            _ownedLaunchAttempted = _ownedLaunch is not null;
        }
        try { return await StartCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { lock (_ownershipGate) _starting = false; }
    }

    private async Task<IOpenCodeServerInstance> StartCoreAsync(CancellationToken cancellationToken)
    {
        var executablePath = await ResolveExecutablePathAsync(cancellationToken).ConfigureAwait(false);
        var instanceId = _ownedLaunch?.ExecutionId ?? "opencode-server-" + Guid.NewGuid().ToString("N");
        var startedAtUtc = _timeProvider.GetUtcNow();
        var lifetime = new CancellationTokenSource();
        var scanner = new OpenCodeServerOutputScanner();
        var portSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputProgress = new ServerOutputProgress(scanner, portSource, lifetime);

        long processGeneration = 0;
        var specification = CreateStartSpecification(instanceId, executablePath,
            generation => Interlocked.CompareExchange(ref processGeneration, generation, 0));

        Task<ProcessExecutionResult> executionTask;

        try
        {
            executionTask = _processSupervisor.ExecuteAsync(specification, outputProgress, lifetime.Token);
        }
        catch
        {
            // A synchronous arbitrary supervisor failure does not prove that no OS process was created.
            var retained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingStartupCleanup[instanceId] = retained.Task;
            _ = ObserveSynchronousSupervisorFailureAsync(lifetime, retained);
            await retained.Task.WaitAsync(_options.DisposeTimeout).ConfigureAwait(false);
            RetireIsolatedPassword(instanceId);
            throw TerminationUnconfirmed();
        }

        int assignedPort;

        try
        {
            assignedPort = await WaitForAssignedPortAsync(executionTask, portSource.Task, cancellationToken)
                .ConfigureAwait(false);

            if (executionTask.IsCompleted)
            {
                throw new OpenCodeServerStartupException(
                    "The OpenCode server process exited immediately after reporting its listening port.");
            }

            int? processId;
            try { processId = _processIdResolver.ResolveChildProcessId(executablePath, startedAtUtc); }
            catch (Exception)
            { throw new OpenCodeServerStartupException("Не удалось передать владение запущенным сервером OpenCode; требуется подтверждённая очистка."); }
            var instance = new OpenCodeServerInstance(
                this, instanceId, processId, assignedPort,
                new Uri($"http://{_options.Hostname}:{assignedPort}", UriKind.Absolute),
                startedAtUtc, lifetime, executionTask, Volatile.Read(ref processGeneration));
            if (_passwordsByInstance.TryGetValue(instanceId, out var password))
                _managedCredential?.BindPort(assignedPort, password);
            outputProgress.PublishIfCaptureHealthy(() => _instances[instanceId] = instance);
            return instance;
        }
        catch
        {
            // Publish retained ownership before cancellation can synchronously complete the supervisor result.
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingStartupCleanup[instanceId] = cleanup.Task;
            _ = ObserveStartupCleanupAsync(instanceId, lifetime, executionTask, cleanup);
            await cleanup.Task.WaitAsync(_options.DisposeTimeout).ConfigureAwait(false);
            RetireIsolatedPassword(instanceId);
            throw;
        }

    }

    public async Task StopServerAsync(IOpenCodeServerInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not OpenCodeServerInstance tracked || !tracked.IsOwnedBy(this))
        {
            throw new ArgumentException(
                "The instance was not created by this OpenCode server manager.",
                nameof(instance));
        }

        if (!_instances.TryGetValue(tracked.InstanceId, out var owned) || !ReferenceEquals(owned, tracked))
        {
            if (!tracked.IsTerminationConfirmed) throw TerminationUnconfirmed();
            return;
        }

        await tracked.GetOrStartStop(() => StopCoreAsync(tracked))
            .WaitAsync(_options.DisposeTimeout + _options.DisposeTimeout).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(OpenCodeServerInstance tracked)
    {
        try
        {
            await RequestInstanceDisposeAsync(tracked.BaseUrl, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await TerminateProcessTreeAsync(tracked.InstanceId, tracked.Lifetime, tracked.ExecutionTask).ConfigureAwait(false);
            tracked.ConfirmTermination();
            _instances.TryRemove(new KeyValuePair<string, OpenCodeServerInstance>(tracked.InstanceId, tracked));
            RetireIsolatedPassword(tracked.InstanceId);
        }
    }

    private async Task ObserveStartupCleanupAsync(string id, CancellationTokenSource lifetime,
        Task<ProcessExecutionResult> execution, TaskCompletionSource completion)
    {
        try
        {
            await TerminateProcessTreeAsync(id, lifetime, execution).ConfigureAwait(false);
            _pendingStartupCleanup.TryRemove(id, out _);
            completion.TrySetResult();
        }
        catch (Exception)
        {
            // Retain the failed owner and prohibit replacement; no unattended native restart is attempted.
            completion.TrySetException(TerminationUnconfirmed());
            _ = completion.Task.Exception;
            _logger.LogWarning("OpenCode startup cleanup is unconfirmed; ownership and replacement exclusion remain active.");
        }
    }

    private async Task ObserveSynchronousSupervisorFailureAsync(CancellationTokenSource lifetime, TaskCompletionSource completion)
    {
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception)
        { _logger.LogWarning("OpenCode startup cancellation failed ({ExceptionType}); ownership remains unresolved.", exception.GetType().Name); }
        finally
        {
            lifetime.Dispose();
            completion.TrySetException(TerminationUnconfirmed());
            _ = completion.Task.Exception;
        }
    }

    private async Task<string> ResolveExecutablePathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.CustomExecutablePath))
        {
            var customPath = _options.CustomExecutablePath!;

            if (!File.Exists(customPath))
            {
                throw new OpenCodeServerStartupException(
                    $"The configured OpenCode executable path '{customPath}' does not exist.");
            }

            return customPath;
        }

        var discovery = await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        if (!discovery.IsInstalled || discovery.ExecutablePath is null)
        {
            throw new OpenCodeServerStartupException(
                discovery.ErrorMessage ?? "The OpenCode CLI executable was not discovered.");
        }

        if (!discovery.IsSupportedVersion)
        {
            throw new OpenCodeServerStartupException(
                $"The discovered OpenCode CLI version '{discovery.Version ?? "unknown"}' is not supported. " +
                $"Minimum supported version is {OpenCodeVersionBaseline.MinimumSupportedVersion}.");
        }

        return discovery.ExecutablePath;
    }

    private ProcessStartSpecification CreateStartSpecification(string instanceId, string executablePath, Action<long> processStarted)
    {
        var (fileName, arguments) = OpenCodeCommandLine.Create(
            executablePath,
            OpenCodeCommandLine.CreateServeArguments(_options.Hostname, _options.Port));

        return new ProcessStartSpecification
        {
            ExecutionId = instanceId,
            ProcessStarted = processStarted,
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = _ownedLaunch?.WorkingDirectory,
            InheritEnvironment = false,
            EnvironmentVariables = _ownedLaunch?.EnvironmentVariables ?? CreateIsolatedEnvironment(executablePath, instanceId),
            StdinPolicy = ProcessStdinPolicy.Closed
        };
    }

    private const string IsolatedServerConfig =
        """{"plugin":[],"mcp":{},"share":"disabled","autoupdate":false}""";

    private Dictionary<string, string> CreateIsolatedEnvironment(string executablePath, string instanceId)
    {
        var systemRoot = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, ".."));
        var powerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0");
        var executableDirectory = Path.GetDirectoryName(executablePath);
        var path = string.IsNullOrEmpty(executableDirectory)
            ? Environment.SystemDirectory + Path.PathSeparator + powerShell
            : Environment.SystemDirectory + Path.PathSeparator + powerShell + Path.PathSeparator + executableDirectory;
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new OpenCodeServerStartupException("An isolated OpenCode home cannot be created without a local application directory.");
        var home = Path.Combine(localApplicationData, "LLMWorkGUI", "opencode-managed", instanceId);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["APPDATA"] = Path.Combine(home, "roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "local"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["TEMP"] = Path.Combine(home, "temp"),
            ["TMP"] = Path.Combine(home, "temp"),
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(home, "config", "opencode")
        };
        foreach (var directory in directories.Values) Directory.CreateDirectory(directory);

        var environment = new Dictionary<string, string>(directories, StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["windir"] = systemRoot,
            ["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["PATH"] = path,
            ["OPENCODE_CONFIG_CONTENT"] = IsolatedServerConfig,
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "true",
            ["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "true",
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "true"
        };
        if (_managedCredential is not null)
        {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _passwordsByInstance[instanceId] = password;
            _managedCredential.Publish(password);
            environment["OPENCODE_SERVER_USERNAME"] = OpenCodeManagedServerCredential.Username;
            environment["OPENCODE_SERVER_PASSWORD"] = password;
        }

        return environment;
    }

    private void RetireIsolatedPassword(string instanceId)
    {
        if (!_passwordsByInstance.Remove(instanceId, out var password)) return;
        _managedCredential?.ClearPassword(password);
    }

    private async Task<int> WaitForAssignedPortAsync(
        Task<ProcessExecutionResult> executionTask,
        Task<int> portTask,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.StartupTimeout);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var completed = await Task.WhenAny(portTask, executionTask, timeoutTask).ConfigureAwait(false);

        if (completed == portTask)
        {
            return await portTask.ConfigureAwait(false);
        }

        if (completed == executionTask)
        {
            ProcessExecutionResult result;

            try
            {
                result = await executionTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new OpenCodeServerStartupException(
                    $"The OpenCode server process failed to start: {exception.Message}",
                    exception);
            }

            var exitCode = result.ExitCode is { } code
                ? code.ToString(CultureInfo.InvariantCulture)
                : "unknown";

            throw new OpenCodeServerStartupException(
                $"The OpenCode server process exited before reporting a listening port (exit code {exitCode}).");
        }

        cancellationToken.ThrowIfCancellationRequested();

        throw new OpenCodeServerStartupException(
            $"The OpenCode server did not report a listening port within {_options.StartupTimeout}.");
    }

    private async Task RequestInstanceDisposeAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        using var disposeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        disposeCts.CancelAfter(_options.DisposeTimeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(baseUrl, OpenCodeApiPaths.InstanceDispose));
            _managedCredential?.TryApply(request);

            var pendingResponse = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, disposeCts.Token);
            HttpResponseMessage response;
            try { response = await pendingResponse.WaitAsync(disposeCts.Token).ConfigureAwait(false); }
            catch
            {
                _ = DisposeLateResponseAsync(pendingResponse);
                throw;
            }
            using var responseOwner = response;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The OpenCode instance dispose request to {BaseUrl} returned status code {StatusCode}.",
                    baseUrl,
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The OpenCode instance dispose request to {BaseUrl} timed out after {DisposeTimeout}.",
                baseUrl,
                _options.DisposeTimeout);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("The OpenCode instance dispose request failed ({ExceptionType}); process termination is still required.", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("OpenCode dispose acknowledgement failed ({ExceptionType}); process termination is still required.", exception.GetType().Name);
        }
    }

    private async Task TerminateProcessTreeAsync(
        string executionId,
        CancellationTokenSource lifetime,
        Task<ProcessExecutionResult> executionTask)
    {
        try
        {
            if (!lifetime.IsCancellationRequested) await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("A process cancellation callback failed ({ExceptionType}); termination is still being observed.", exception.GetType().Name);
        }
        try
        {
            var result = await executionTask.ConfigureAwait(false);
            if (result.ExecutionId != executionId || !Enum.IsDefined(result.TerminationReason)) throw TerminationUnconfirmed();
            if (result.TerminationReason is ProcessTerminationReason.StartupPending or ProcessTerminationReason.CleanupPending)
                await _processSupervisor.WaitForStartupCleanupAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fault/cancel/unsupported cleanup is not physical termination. Never drop this owner on those paths.
            throw TerminationUnconfirmed();
        }
        finally { lifetime.Dispose(); }
    }

    private static OpenCodeServerStartupException TerminationUnconfirmed() => new(
        "Завершение принадлежащего приложению дерева OpenCode не подтверждено. Владение сохранено; замена процесса и повторный запуск запрещены.");

    private static async Task DisposeLateResponseAsync(Task<HttpResponseMessage> response)
    {
        try { (await response.ConfigureAwait(false)).Dispose(); }
        catch { /* The bounded request path already reported the transport failure. */ }
    }

    private sealed class ServerOutputProgress : IProgress<ProcessOutputEvent>, IProcessOutputCaptureObserver
    {
        private readonly object _captureGate = new();
        private readonly OpenCodeServerOutputScanner _scanner;
        private readonly TaskCompletionSource<int> _portSource;
        private readonly CancellationTokenSource _lifetime;
        private bool _captureIncomplete;

        public ServerOutputProgress(OpenCodeServerOutputScanner scanner, TaskCompletionSource<int> portSource,
            CancellationTokenSource lifetime)
        {
            _scanner = scanner;
            _portSource = portSource;
            _lifetime = lifetime;
        }

        public void OnOutputCaptureFailure(ProcessStreamKind streamKind)
        {
            lock (_captureGate)
            {
                _captureIncomplete = true;
                _portSource.TrySetException(CaptureFailure());
                // Retirement is visible to IsAlive before cancellation can invoke any callbacks.
                _lifetime.Cancel();
            }
        }

        public void PublishIfCaptureHealthy(Action publish)
        {
            lock (_captureGate)
            {
                if (_captureIncomplete) throw CaptureFailure();
                publish();
            }
        }

        public void Report(ProcessOutputEvent value)
        {
            lock (_captureGate)
            {
                if (_captureIncomplete || _portSource.Task.IsCompleted) return;

                if (_scanner.Append(value.Text) && _scanner.AssignedPort is { } port)
                    _portSource.TrySetResult(port);
            }
        }

        private static OpenCodeServerStartupException CaptureFailure() => new(
            "Захват вывода OpenCode неполон; запуск или продолжение принадлежащего приложения сервера отклонено.");
    }
}

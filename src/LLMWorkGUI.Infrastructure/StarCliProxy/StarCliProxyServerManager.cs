using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.StarCliProxy;

/// <summary>
/// Owns managed loopback star-cliproxy instances (ТЗ §6.11a, ADR-0007). Each instance is launched
/// through the shared process supervisor, receives the generated configuration file from the run
/// directory outside the project root, and gets the account context (for example CODEX_HOME) only
/// inside its own process environment. When the gateway cannot be located the manager reports a
/// pure degraded mode with an exact blocker and no OpenCode/direct-CLI fallback.
/// </summary>
public sealed class StarCliProxyServerManager : IStarCliProxyServerManager, IStarCliProxyAvailabilityProbe
{
    public const string InstanceIdPrefix = "star-cliproxy-";

    public const string ProxyApiKeyPrefix = "sk-proxy-";

    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly StarCliProxyOptions _options;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly IStarCliProxyClient _client;
    private readonly StorageOptions _storageOptions;
    private readonly ISecretStore? _secretStore;
    private readonly IProcessIdResolver? _processIdResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StarCliProxyServerManager> _logger;
    private readonly StarCliProxyExecutableResolution _resolution;
    private readonly ConcurrentDictionary<string, StarCliProxyServerInstance> _instances =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingSecretCleanup =
        new(StringComparer.Ordinal);

    public StarCliProxyServerManager(
        IOptions<StarCliProxyOptions> options,
        IStarCliProxyExecutableResolver executableResolver,
        IProcessSupervisor processSupervisor,
        IStarCliProxyClient client,
        StorageOptions storageOptions,
        ISecretStore? secretStore = null,
        IProcessIdResolver? processIdResolver = null,
        TimeProvider? timeProvider = null,
        ILogger<StarCliProxyServerManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executableResolver);
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _options = options.Value;
        _options.Validate();
        _processSupervisor = processSupervisor;
        _client = client;
        _storageOptions = storageOptions;
        _secretStore = secretStore;
        _processIdResolver = processIdResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<StarCliProxyServerManager>.Instance;
        _resolution = ResolveExecutable(executableResolver);
    }

    public bool IsAvailable => _resolution.IsAvailable;

    public string? AvailabilityBlocker => _resolution.Blocker;

    public async Task<IStarCliProxyServerInstance> StartServerAsync(
        StarCliProxyServerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionId);

        if (!IsAvailable)
        {
            throw new StarCliProxyStartupException(AvailabilityBlocker!);
        }

        if (request.CodexHomePath is { } codexHomePath)
        {
            if (!Path.IsPathFullyQualified(codexHomePath))
            {
                throw new StarCliProxyStartupException(
                    $"CODEX_HOME '{codexHomePath}' must be an absolute path; relative homes are forbidden (ТЗ §6.11a).");
            }

            if (!Directory.Exists(codexHomePath))
            {
                throw new StarCliProxyStartupException(
                    $"The CODEX_HOME directory '{codexHomePath}' does not exist. Create and authenticate it " +
                    "independently before starting the proxy; contexts are never copied or created implicitly.");
            }
        }

        var instanceId = InstanceIdPrefix + Guid.NewGuid().ToString("N");
        var startedAtUtc = _timeProvider.GetUtcNow();
        var assignedPort = _options.Port > 0 ? _options.Port : ReserveLoopbackPort();
        var providerIds = ValidateProviderIds(request.EnabledProviderIds);
        var runDirectory = AppDataPaths.GetRunDirectory(ResolveDataRoot(), request.ExecutionId);
        var key = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = key.Value;
        var adminToken = GenerateToken(32);
        string configPath;
        ProcessStartSpecification specification;
        try
        {
            configPath = await StarCliProxyConfigWriter.WriteAsync(
                runDirectory,
                _options.ConfigFileName,
                _options.Hostname,
                assignedPort,
                providerIds,
                adminToken,
                apiKey,
                cancellationToken)
                .ConfigureAwait(false);
            specification = CreateStartSpecification(
                request.ExecutionId,
                configPath,
                apiKey,
                adminToken,
                request.CodexHomePath);
        }
        catch
        {
            // No process was invoked. Only this attempt's freshly created reference is owned.
            await ObservePrelaunchSecretCleanupAsync(key.GeneratedReference).ConfigureAwait(false);
            throw;
        }

        var lifetime = new CancellationTokenSource();

        Task<ProcessExecutionResult> executionTask;

        try
        {
            executionTask = _processSupervisor.ExecuteAsync(specification, outputProgress: null, lifetime.Token);
        }
        catch
        {
            lifetime.Dispose();
            await ObservePrelaunchSecretCleanupAsync(key.GeneratedReference).ConfigureAwait(false);
            throw;
        }

        var instance = new StarCliProxyServerInstance(
            instanceId, null, assignedPort,
            new Uri($"http://{_options.Hostname}:{assignedPort}/", UriKind.Absolute),
            request.CodexHomePath, configPath, apiKey, startedAtUtc, lifetime, executionTask,
            request.ExecutionId, key.GeneratedReference);
        // Retain even an unpublished failed-start instance until physical cleanup and its key
        // cleanup settle. The observer never retries the native launch or health request.
        _instances[instanceId] = instance;
        instance.CleanupOperation = Task.Run(() => ObserveInstanceCleanupAsync(instance));

        try
        {
            instance.SetProcessId(TryResolveProcessId(startedAtUtc));
            var endpoint = new StarCliProxyEndpoint(
                new Uri($"http://{_options.Hostname}:{assignedPort}/", UriKind.Absolute),
                apiKey);

            await WaitForHealthyAsync(endpoint, executionTask, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CancelOwnedInstance(instance);
            try { await instance.Cleanup.Task.WaitAsync(_options.DisposeTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _logger.LogWarning("Failed-start proxy cleanup remains unconfirmed; instance and generated-reference ownership are retained.");
            }
            throw;
        }

        _logger.LogInformation(
            "Started a managed star-cliproxy instance on loopback port {Port} with {ProviderCount} enabled provider(s).",
            assignedPort,
            providerIds.Count);

        return instance;
    }

    private static IReadOnlyList<string> ValidateProviderIds(IReadOnlyList<string> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);

        var validated = new List<string>(providerIds.Count);

        foreach (var providerId in providerIds)
        {
            if (string.IsNullOrWhiteSpace(providerId) ||
                !providerId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                throw new StarCliProxyStartupException(
                    $"The star-cliproxy provider id '{providerId}' is invalid: only ASCII letters, digits, '-' and '_' are allowed.");
            }

            if (!validated.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            {
                validated.Add(providerId);
            }
        }

        return validated;
    }

    public async Task StopServerAsync(
        IStarCliProxyServerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not StarCliProxyServerInstance tracked)
        {
            throw new ArgumentException(
                "The instance was not created by this star-cliproxy server manager.",
                nameof(instance));
        }

        if (!_instances.TryGetValue(tracked.InstanceId, out var retained) || !ReferenceEquals(retained, tracked))
        {
            return;
        }

        CancelOwnedInstance(tracked);
        // Cancellation/timeout of this wait never cancels the retained cleanup observer.
        // A bounded refusal is not a successful physical-stop claim.
        await tracked.Cleanup.Task.WaitAsync(_options.DisposeTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StarCliProxyHealthStatus> CheckHealthAsync(
        IStarCliProxyServerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not StarCliProxyServerInstance tracked)
        {
            throw new ArgumentException(
                "The instance was not created by this star-cliproxy server manager.",
                nameof(instance));
        }

        var endpoint = new StarCliProxyEndpoint(tracked.BaseUrl, tracked.ApiKey);

        return await _client.CheckHealthAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    private StarCliProxyExecutableResolution ResolveExecutable(IStarCliProxyExecutableResolver executableResolver)
    {
        if (string.IsNullOrWhiteSpace(_options.CustomExecutablePath))
        {
            return executableResolver.Resolve();
        }

        var customPath = _options.CustomExecutablePath!;

        return File.Exists(customPath)
            ? StarCliProxyExecutableResolution.Found(customPath)
            : StarCliProxyExecutableResolution.NotFound(
                $"The configured star-cliproxy executable '{customPath}' does not exist. " +
                StarCliProxyPolicy.NotInstalledBlocker);
    }

    private ProcessStartSpecification CreateStartSpecification(
        string executionId,
        string configPath,
        string apiKey,
        string adminToken,
        string? codexHomePath)
    {
        var arguments = _options.StartArguments
            .Select(argument => argument.Replace(
                StarCliProxyOptions.ConfigPathPlaceholder,
                configPath,
                StringComparison.Ordinal))
            .ToList();

        var (fileName, launchArguments) = BuildLaunchCommand(_resolution.ExecutablePath!, arguments);

        var environment = ProcessRuntimeEnvironment.CreateBaseline(_resolution.ExecutablePath!);
        environment["PROXY_API_KEY"] = apiKey;
        environment["ADMIN_TOKEN"] = adminToken;

        if (codexHomePath is not null)
        {
            // Account context is passed only to the owned process environment; global
            // HOME/USERPROFILE are never modified (ADR-0007 §4).
            environment["CODEX_HOME"] = codexHomePath;
        }

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = fileName,
            Arguments = launchArguments,
            InheritEnvironment = false,
            EnvironmentVariables = environment,
            StdinPolicy = ProcessStdinPolicy.Closed
        };
    }

    private static (string FileName, IReadOnlyList<string> Arguments) BuildLaunchCommand(
        string executablePath,
        IReadOnlyList<string> launchArguments)
    {
        var extension = Path.GetExtension(executablePath);
        InterpreterLaunchGuard.RejectInterpreterEntry(executablePath, launchArguments);

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var commandInterpreterArguments = new List<string> { "/d", "/c", executablePath };
            commandInterpreterArguments.AddRange(launchArguments);

            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), commandInterpreterArguments);
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powerShellArguments = new List<string>
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                executablePath
            };
            powerShellArguments.AddRange(launchArguments);

            return (
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                powerShellArguments);
        }

        return (executablePath, launchArguments);
    }

    private async Task WaitForHealthyAsync(
        StarCliProxyEndpoint endpoint,
        Task<ProcessExecutionResult> executionTask,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                if (executionTask.IsCompleted)
                {
                    ProcessExecutionResult result;

                    try
                    {
                        result = await executionTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new StarCliProxyStartupException(
                            "The managed star-cliproxy process was cancelled before it became healthy.");
                    }

                    var exitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

                    throw new StarCliProxyStartupException(
                        $"The star-cliproxy process exited before becoming healthy " +
                        $"(exit code {exitCode}, termination {result.TerminationReason}).");
                }

                var health = await _client.CheckHealthAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);

                if (health.IsHealthy)
                {
                    return;
                }

                await Task.Delay(HealthPollInterval, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StarCliProxyStartupException(
                $"The star-cliproxy gateway at {endpoint.BaseUrl} did not become healthy within " +
                $"{_options.StartupTimeout} (ТЗ §6.11a).");
        }
    }

    private static void CancelOwnedInstance(StarCliProxyServerInstance instance)
    {
        lock (instance.Sync)
        {
            if (!instance.Cleanup.Task.IsCompleted && !instance.Lifetime.IsCancellationRequested)
                instance.Lifetime.Cancel();
        }
    }

    private async Task ObserveInstanceCleanupAsync(StarCliProxyServerInstance instance)
    {
        var warned = false;
        while (true)
        {
            try
            {
                ProcessExecutionResult? result = null;
                try { result = await instance.ExecutionTask.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // ExecuteAsync cancellation is the existing pre-start/no-result contract.
                    // Pending native/physical cleanup is reported explicitly in its result.
                }
                catch (Exception)
                {
                    // A fault alone is not physical-stop evidence. Ask the actual shared owner.
                    await _processSupervisor.WaitForStartupCleanupAsync(instance.ExecutionId).ConfigureAwait(false);
                }
                if (result?.TerminationReason is ProcessTerminationReason.StartupPending or ProcessTerminationReason.CleanupPending)
                    await _processSupervisor.WaitForStartupCleanupAsync(instance.ExecutionId).ConfigureAwait(false);
                instance.ConfirmPhysicalStop();
                await BeginGeneratedSecretCleanup(instance.GeneratedSecretReference).ConfigureAwait(false);
                lock (instance.Sync)
                {
                    instance.Cleanup.TrySetResult();
                    instance.Lifetime.Dispose();
                }
                ((ICollection<KeyValuePair<string, StarCliProxyServerInstance>>)_instances).Remove(new(instance.InstanceId, instance));
                return;
            }
            catch (Exception)
            {
                if (!warned)
                {
                    _logger.LogWarning("Proxy cleanup remains unconfirmed; instance and generated-reference ownership are retained.");
                    warned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
    }

    private async Task ObservePrelaunchSecretCleanupAsync(string? reference)
    {
        try { await BeginGeneratedSecretCleanup(reference).WaitAsync(_options.DisposeTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Prelaunch generated-reference cleanup remains pending and retains ownership.");
        }
    }

    private Task BeginGeneratedSecretCleanup(string? reference)
    {
        if (reference is null || _secretStore is null) return Task.CompletedTask;
        var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = _pendingSecretCleanup.GetOrAdd(reference, candidate);
        if (ReferenceEquals(owner, candidate)) _ = Task.Run(async () =>
        {
            var warned = false;
            while (true)
            {
                try
                {
                    // Only references returned by this manager's fresh SaveSecret are passed here.
                    await _secretStore.DeleteSecretAsync(reference, CancellationToken.None).ConfigureAwait(false);
                    ((ICollection<KeyValuePair<string, TaskCompletionSource>>)_pendingSecretCleanup).Remove(new(reference, owner));
                    owner.TrySetResult();
                    return;
                }
                catch (Exception)
                {
                    if (!warned)
                    {
                        _logger.LogWarning("Owned generated-reference cleanup failed; a retained cleanup operation will retry.");
                        warned = true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }
        });
        return owner.Task;
    }

    private sealed record ApiKeyMaterial(string Value, string? GeneratedReference);

    private async Task<ApiKeyMaterial> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKeySecretReference) && _secretStore is not null)
        {
            var stored = await _secretStore
                .GetSecretAsync(_options.ApiKeySecretReference!, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stored))
            {
                return new(stored, null);
            }
        }

        var generated = ProxyApiKeyPrefix + GenerateToken(24);

        string? reference = null;
        if (_secretStore is not null)
        {
            reference = await _secretStore
                .SaveSecretAsync(generated, cancellationToken)
                .ConfigureAwait(false);

            // The reference is a locator, not secret material; the key itself is never logged.
            _logger.LogInformation(
                "Stored the generated star-cliproxy proxy API key under secret reference {SecretReference}.",
                reference);
        }

        return new(generated, reference);
    }

    private int? TryResolveProcessId(DateTimeOffset startedAtUtc)
    {
        if (_processIdResolver is null)
        {
            return null;
        }

        try
        {
            return _processIdResolver.ResolveChildProcessId(_resolution.ExecutablePath!, startedAtUtc);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(exception, "Could not resolve the managed star-cliproxy process id.");
            return null;
        }
    }

    private string ResolveDataRoot() =>
        string.IsNullOrWhiteSpace(_storageOptions.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(_storageOptions.AppDataDirectory);

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GenerateToken(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
}

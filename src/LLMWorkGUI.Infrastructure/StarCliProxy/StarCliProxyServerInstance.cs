using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;

namespace LLMWorkGUI.Infrastructure.StarCliProxy;

internal sealed class StarCliProxyServerInstance : IStarCliProxyServerInstance
{
    public StarCliProxyServerInstance(
        string instanceId,
        int? processId,
        int assignedPort,
        Uri baseUrl,
        string? codexHomePath,
        string configFilePath,
        string apiKey,
        DateTimeOffset startedAtUtc,
        CancellationTokenSource lifetime,
        Task<ProcessExecutionResult> executionTask,
        string executionId,
        string? generatedSecretReference)
    {
        InstanceId = instanceId;
        ProcessId = processId;
        AssignedPort = assignedPort;
        BaseUrl = baseUrl;
        CodexHomePath = codexHomePath;
        ConfigFilePath = configFilePath;
        ApiKey = apiKey;
        StartedAtUtc = startedAtUtc;
        Lifetime = lifetime;
        ExecutionTask = executionTask;
        ExecutionId = executionId;
        GeneratedSecretReference = generatedSecretReference;
    }

    public string InstanceId { get; }

    public int? ProcessId { get; private set; }
    internal void SetProcessId(int? processId) => ProcessId = processId;

    public int AssignedPort { get; }

    public Uri BaseUrl { get; }

    public string? CodexHomePath { get; }

    public string ConfigFilePath { get; }

    public string ApiKey { get; }

    public DateTimeOffset StartedAtUtc { get; }

    private int _physicallyStopped;
    public bool IsAlive => Volatile.Read(ref _physicallyStopped) == 0;
    internal void ConfirmPhysicalStop() => Interlocked.Exchange(ref _physicallyStopped, 1);
    internal object Sync { get; } = new();
    internal TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? CleanupOperation { get; set; }
    internal string ExecutionId { get; }
    internal string? GeneratedSecretReference { get; }

    internal CancellationTokenSource Lifetime { get; }

    internal Task<ProcessExecutionResult> ExecutionTask { get; }
}

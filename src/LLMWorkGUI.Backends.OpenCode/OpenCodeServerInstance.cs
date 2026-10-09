using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;

namespace LLMWorkGUI.Backends.OpenCode;

internal sealed class OpenCodeServerInstance : IOpenCodeServerInstance
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Task<ProcessExecutionResult> _executionTask;
    private readonly OpenCodeServerManager _owner;
    private int? _processId;
    private Task? _stopTask;
    private int _stopRequested;
    private int _terminationConfirmed;

    public OpenCodeServerInstance(
        OpenCodeServerManager owner,
        string instanceId,
        int? processId,
        int assignedPort,
        Uri baseUrl,
        DateTimeOffset startedAtUtc,
        CancellationTokenSource lifetime,
        Task<ProcessExecutionResult> executionTask,
        long processGeneration = 0)
    {
        _owner = owner;
        InstanceId = instanceId;
        _processId = processId;
        AssignedPort = assignedPort;
        BaseUrl = baseUrl;
        StartedAtUtc = startedAtUtc;
        _lifetime = lifetime;
        _executionTask = executionTask;
        ProcessGeneration = processGeneration;
    }

    public string InstanceId { get; }

    public long ProcessGeneration { get; }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _processId;
            }
        }
    }

    public int AssignedPort { get; }

    public Uri BaseUrl { get; }

    public bool IsAlive => !_executionTask.IsCompleted && !_lifetime.IsCancellationRequested;
    public bool IsTerminationConfirmed => Volatile.Read(ref _terminationConfirmed) == 1;
    internal bool StopRequested => Volatile.Read(ref _stopRequested) == 1;
    internal bool IsOwnedBy(OpenCodeServerManager manager) => ReferenceEquals(_owner, manager);
    internal void ConfirmTermination() => Volatile.Write(ref _terminationConfirmed, 1);

    public DateTimeOffset StartedAtUtc { get; }

    internal CancellationTokenSource Lifetime => _lifetime;

    internal Task<ProcessExecutionResult> ExecutionTask => _executionTask;

    internal Task GetOrStartStop(Func<Task> stop)
    {
        lock (_gate)
        {
            Volatile.Write(ref _stopRequested, 1);
            return _stopTask ??= stop();
        }
    }

    internal void UpdateProcessId(int? processId)
    {
        lock (_gate)
        {
            _processId = processId;
        }
    }
}

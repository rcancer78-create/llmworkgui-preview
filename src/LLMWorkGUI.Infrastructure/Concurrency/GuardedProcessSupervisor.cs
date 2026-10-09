using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Processes;

namespace LLMWorkGUI.Infrastructure.Concurrency;

public sealed class GuardedProcessSupervisor : IProcessSupervisor
{
    private readonly IProcessSupervisor _inner;
    private readonly IApplicationInstanceGuard _instanceGuard;

    public GuardedProcessSupervisor(IProcessSupervisor inner, IApplicationInstanceGuard instanceGuard)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(instanceGuard);

        _inner = inner;
        _instanceGuard = instanceGuard;
    }

    public IProcessSupervisor Inner => _inner;
    public Task WaitForStartupCleanupAsync(string executionId, CancellationToken cancellationToken = default) =>
        _inner.WaitForStartupCleanupAsync(executionId, cancellationToken);

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessStartSpecification specification,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default)
    {
        _instanceGuard.EnsureSupervisorPermitted();

        return _inner.ExecuteAsync(specification, outputProgress, cancellationToken);
    }

    public Task<IProtocolProcessSession> StartProtocolProcessAsync(
        ProcessStartSpecification specification,
        CancellationToken cancellationToken = default)
    {
        _instanceGuard.EnsureSupervisorPermitted();

        return _inner.StartProtocolProcessAsync(specification, cancellationToken);
    }
}

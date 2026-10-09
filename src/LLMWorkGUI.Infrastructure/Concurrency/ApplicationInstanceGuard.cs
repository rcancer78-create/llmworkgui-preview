using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.Infrastructure.Concurrency;

public sealed class ApplicationInstanceGuard : IApplicationInstanceGuard
{
    public const string ViewOnlyReason =
        "Another LLM Work GUI instance already owns the supervisor for this app-data directory.";

    private readonly NamedMutexScope? _supervisorScope;
    private bool _disposed;

    public ApplicationInstanceGuard(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        InstanceId = Guid.NewGuid().ToString("D");
        _supervisorScope = NamedMutexScope.TryAcquire(NamedMutexNames.ForSupervisor(appDataDirectory));
        IsPrimarySupervisor = _supervisorScope is not null;
    }

    public string InstanceId { get; }

    public bool IsPrimarySupervisor { get; }

    public bool IsViewOnly => !IsPrimarySupervisor;

    public void EnsureSupervisorPermitted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsViewOnly)
        {
            throw new SecondaryInstanceReadOnlyException(
                $"{ViewOnlyReason} This instance runs in View-Only mode: supervisor and writer operations are disabled.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _supervisorScope?.Dispose();
    }
}

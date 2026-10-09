namespace LLMWorkGUI.Application.Concurrency;

public interface IApplicationInstanceGuard : IDisposable
{
    string InstanceId { get; }

    bool IsPrimarySupervisor { get; }

    bool IsViewOnly { get; }

    void EnsureSupervisorPermitted();
}

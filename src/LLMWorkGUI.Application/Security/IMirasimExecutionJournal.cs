using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Security;

public sealed record MirasimExecutionTarget(ProjectProviderContext Context, string RootPath, string NativeSessionId,
    string NativeModelId, string Harness, string ExecutionId, string? RequestedRouteId, string PromptSha256,
    bool RequiresWriterLock, long ProcessGeneration);
public sealed record MirasimExecutionAdmission(MirasimExecutionTarget Target, string SessionId, string RouteId,
    string AccountId, string ModelId, string PolicyFingerprint, string RouteFingerprint);

public interface IMirasimExecutionJournal
{
    Task<MirasimExecutionAdmission> BeginAsync(MirasimExecutionTarget target, string policyFingerprint, CancellationToken cancellationToken);
    Task MarkRunningAsync(MirasimExecutionAdmission entry, string body, CancellationToken cancellationToken);
    /// <summary>Authorizes control of an admitted execution without reapplying new-turn data/route policy.</summary>
    Task AuthorizeOwnedOperationAsync(MirasimExecutionAdmission entry, CancellationToken cancellationToken);
    Task CompleteAsync(MirasimExecutionAdmission entry, ExecutionState state, ExecutionFailureReason reason, CancellationToken cancellationToken);
}

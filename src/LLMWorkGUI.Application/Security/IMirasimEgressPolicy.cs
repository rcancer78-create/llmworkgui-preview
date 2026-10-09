using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Security;

public sealed record MirasimEgressOperation(ProjectProviderContext Context, string RootPath,
    string Endpoint, string Operation, string SessionKey, string Harness, string ModelId,
    string? ExecutionId, string Body)
{
    public string? ExpectedPolicyFingerprint { get; init; }
}

/// <summary>Validates current stored metadata; never grants Restricted repository access.</summary>
public interface IMirasimEgressPolicy
{
    Task<string> ValidateAsync(ProjectProviderContext context, string rootPath, CancellationToken cancellationToken);
    Task AuthorizeAsync(MirasimEgressOperation operation, CancellationToken cancellationToken);
}

public sealed class MirasimEgressPolicyException : InvalidOperationException
{
    public MirasimEgressPolicyException() : this("Передача Mirasim заблокирована: требуется действующая привязка проекта, профиля и разрешающая классификация данных.") { }
    public MirasimEgressPolicyException(string message) : base(message) { }
}

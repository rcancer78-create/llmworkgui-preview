using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

public sealed record SessionBinding
{
    public SessionBinding(
        BackendType backend,
        string providerProfileId,
        string accountId,
        string modelId,
        string? reasoningEffort,
        string? speedMode,
        string? executionMode)
    {
        Backend = backend;
        ProviderProfileId = DomainGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        AccountId = DomainGuard.NotBlank(accountId, nameof(accountId));
        ModelId = DomainGuard.NotBlank(modelId, nameof(modelId));
        ReasoningEffort = DomainGuard.OptionalNotBlank(reasoningEffort, nameof(reasoningEffort));
        SpeedMode = DomainGuard.OptionalNotBlank(speedMode, nameof(speedMode));
        ExecutionMode = DomainGuard.OptionalNotBlank(executionMode, nameof(executionMode));
    }

    public BackendType Backend { get; }

    public string ProviderProfileId { get; }

    public string AccountId { get; }

    public string ModelId { get; }

    public string? ReasoningEffort { get; }

    public string? SpeedMode { get; }

    public string? ExecutionMode { get; }
}

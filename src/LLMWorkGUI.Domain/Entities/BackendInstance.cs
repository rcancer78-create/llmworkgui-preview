using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class BackendInstance
{
    public BackendInstance(
        string id,
        string providerProfileId,
        BackendType backend,
        int? processId,
        string? endpoint,
        string? version,
        HealthState health,
        DateTimeOffset? startedAt)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        ProviderProfileId = DomainGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        Backend = backend;
        ProcessId = processId;
        Endpoint = DomainGuard.OptionalNotBlank(endpoint, nameof(endpoint));
        Version = DomainGuard.OptionalNotBlank(version, nameof(version));
        Health = health;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public string ProviderProfileId { get; }

    public BackendType Backend { get; }

    public int? ProcessId { get; }

    public string? Endpoint { get; }

    public string? Version { get; }

    public HealthState Health { get; }

    public DateTimeOffset? StartedAt { get; }
}

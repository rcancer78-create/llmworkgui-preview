using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Accounts;

public sealed record AccountSettings(string Id, string ProfileId, string Name, string? NativeId,
    int Priority, bool IsEnabled, int MaxConcurrentExecutions, double? ReserveThreshold);
public sealed record AccountConfigurationRow(AccountSettings Settings, AuthState AuthState,
    HealthState Health, string? CooldownUntil, string? DisabledUntil)
{
    /// <summary>Persisted requested bindings, not native response-origin evidence.</summary>
    public IReadOnlyList<AccountSessionBinding> Sessions { get; init; } = [];
    public int SessionCount { get; init; }
}
public sealed record AccountSessionBinding(string LocalSessionId, string ProjectId, BackendType Backend,
    string ModelId, string? ReasoningEffort, string? SpeedMode, string? ExecutionMode, string? NativeSessionId,
    SessionState State, string? ActiveExecutionId);
public sealed record AccountConfiguration(IReadOnlyList<ModelProfileOption> Profiles,
    IReadOnlyList<AccountConfigurationRow> Accounts);
public sealed record SaveAccountSettings(string ProfileId, string Name, int Priority,
    bool IsEnabled, int MaxConcurrentExecutions, double? ReserveThreshold, AccountSettings? Expected = null);
public sealed record AccountImportCandidate(string Id, string Name, string? NativeId);

/// <summary>Local metadata only: discovery never authorizes an account or proves response identity.</summary>
public interface IAccountConfigurationService
{
    Task<AccountConfiguration> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> SaveAsync(SaveAccountSettings request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountImportCandidate>> DiscoverAsync(string profileId, CancellationToken cancellationToken = default);
    Task<string> ImportAsync(string profileId, AccountImportCandidate expected, CancellationToken cancellationToken = default);
}

using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public interface IMirasimSessionLifecycleService
{
    Task<MirasimSessionBinding> CreateSessionAsync(
        string instanceId,
        string harness,
        string modelId,
        string workspacePath,
        string? authToken = null,
        CancellationToken cancellationToken = default,
        ProjectProviderContext? projectContext = null);

    Task<MirasimSessionBinding> ContinueSessionAsync(
        MirasimSessionBinding existingBinding,
        string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<MirasimTurnResult> ExecuteTurnAsync(
        MirasimTurnRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MirasimStreamEvent> WatchTurnAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<MirasimTurnResult> CancelTurnAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        CancellationToken cancellationToken = default);

    Task<MirasimTurnResult> ReconcileTurnAsync(
        string sessionKey,
        string turnId,
        string? authToken = null,
        CancellationToken cancellationToken = default);
}

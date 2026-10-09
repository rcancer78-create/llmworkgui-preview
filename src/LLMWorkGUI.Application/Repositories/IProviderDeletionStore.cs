namespace LLMWorkGUI.Application.Repositories;

/// <summary>Commits profile deletion, revocation and the reference-only payload cleanup queue together.</summary>
public interface IProviderDeletionStore
{
    Task<bool> DeleteConfigurationAsync(string providerId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListPendingSecretDeletionsAsync(CancellationToken cancellationToken = default);
    Task CompleteSecretDeletionAsync(string reference, CancellationToken cancellationToken = default);
}

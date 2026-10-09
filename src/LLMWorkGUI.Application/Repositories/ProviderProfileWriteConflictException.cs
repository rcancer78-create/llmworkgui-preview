namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// Proof from a transactional repository that its expected-revision write was refused before commit.
/// Carried as the inner exception of InvalidOperationException to preserve the existing caller contract.
/// Transport/commit-acknowledgement failures must never use this marker.
/// </summary>
public sealed class ProviderProfileWriteConflictException : Exception
{
    public ProviderProfileWriteConflictException() : base("The expected provider revision did not match; no profile write committed.") { }
}

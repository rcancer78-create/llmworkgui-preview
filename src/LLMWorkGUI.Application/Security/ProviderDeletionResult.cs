namespace LLMWorkGUI.Application.Security;

public sealed record ProviderDeletionResult(bool Deleted, int PendingSecretCleanup);

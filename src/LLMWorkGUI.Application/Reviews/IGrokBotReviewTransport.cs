namespace LLMWorkGUI.Application.Reviews;

public interface IGrokBotReviewTransport
{
    Task<bool> CheckSessionAsync(string nodeExecutable, CancellationToken cancellationToken);
    Task<(string Text, bool CleanupPending)> ReviewAsync(string nodeExecutable, string prompt, CancellationToken cancellationToken);
    /// <summary>Consent-bound transport must enforce the last permissible send time itself.</summary>
    Task<(string Text, bool CleanupPending)> ReviewAsync(string nodeExecutable, string prompt,
        DateTimeOffset notAfterUtc, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Транспорт не поддерживает срок согласия на передачу.");
}

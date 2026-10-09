namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Service for performing non-destructive connection health checks against configured LLM provider base URLs.
/// </summary>
public interface IProviderConnectionTestService
{
    /// <summary>
    /// Executes a connection test against the given provider settings.
    /// </summary>
    /// <param name="settings">Provider settings containing BaseUrl, CustomHeaders, and optional ApiKeySecretRef.</param>
    /// <param name="explicitApiKey">Optional explicit API key overriding secret store resolution (e.g. from an unsaved form input).</param>
    /// <param name="timeout">Optional timeout duration (defaults to 8 seconds).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured result containing classified status, latency, discovered models, or sanitized error message.</returns>
    Task<ProviderConnectionTestResult> TestConnectionAsync(
        CustomProviderSettings settings,
        string? explicitApiKey = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

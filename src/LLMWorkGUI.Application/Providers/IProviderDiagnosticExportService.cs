namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Service for generating exportable, fully redacted diagnostic reports for providers.
/// </summary>
public interface IProviderDiagnosticExportService
{
    Task<ProviderDiagnosticReport> GenerateExportAsync(
        CustomProviderSettings settings,
        ProviderConnectionTestResult? connectionResult = null,
        IReadOnlyList<PluginInfo>? plugins = null,
        CancellationToken cancellationToken = default);
}

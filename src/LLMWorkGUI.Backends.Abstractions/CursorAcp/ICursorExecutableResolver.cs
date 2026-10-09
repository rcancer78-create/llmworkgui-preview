namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Locates the Cursor Agent CLI on the current Windows user account and probes its version
/// (ТЗ §6.2, §6.12). The resolver never throws for an absent or unusable CLI: it returns a
/// typed <see cref="CursorAcpExecutableResolution"/> so the backend can enter a clean degraded mode.
/// </summary>
public interface ICursorExecutableResolver
{
    Task<CursorAcpExecutableResolution> ResolveAsync(CancellationToken cancellationToken = default);
}

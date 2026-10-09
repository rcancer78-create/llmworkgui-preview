namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Locates the managed star-cliproxy gateway on the current Windows user account
/// (configured path, environment override, PATH, %LOCALAPPDATA%) without touching credentials.
/// </summary>
public interface IStarCliProxyExecutableResolver
{
    StarCliProxyExecutableResolution Resolve();
}

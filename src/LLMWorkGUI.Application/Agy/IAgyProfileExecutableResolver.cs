namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Locates the user-selected agy-profile utility: first on PATH, then in
/// <c>%LOCALAPPDATA%\agy-profile</c> (ТЗ §6.11a).
/// </summary>
public interface IAgyProfileExecutableResolver
{
    AgyProfileExecutableResolution Resolve();
}

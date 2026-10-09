namespace LLMWorkGUI.Application.Cli;

public interface ICliExecutableLocator
{
    Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default);
}

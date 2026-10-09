namespace LLMWorkGUI.Backends.OpenCode;

public interface IProcessIdResolver
{
    int? ResolveChildProcessId(string executablePath, DateTimeOffset startedAfterUtc);
}

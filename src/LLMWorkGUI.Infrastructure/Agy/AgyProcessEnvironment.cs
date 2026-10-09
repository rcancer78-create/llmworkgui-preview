using LLMWorkGUI.Backends.Abstractions.Processes;

namespace LLMWorkGUI.Infrastructure.Agy;

/// <summary>
/// Environment for an agy-profile or agy CLI child. The baseline does not copy the parent
/// environment. USERPROFILE, APPDATA and LOCALAPPDATA are passed explicitly because
/// list/current/switch and the following agy run read the account store under that profile.
/// </summary>
internal static class AgyProcessEnvironment
{
    private static readonly string[] AccountLocationVariables = ["USERPROFILE", "APPDATA", "LOCALAPPDATA"];

    public static Dictionary<string, string> Create(string executablePath)
    {
        var environment = ProcessRuntimeEnvironment.CreateBaseline(executablePath);
        foreach (var name in AccountLocationVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                environment[name] = value;
        }

        return environment;
    }
}

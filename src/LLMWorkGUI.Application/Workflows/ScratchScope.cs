namespace LLMWorkGUI.Application.Workflows;

public enum ScratchScope
{
    Preview,
    Draft,
    Adaptation,
    Run
}

public static class ScratchScopeExtensions
{
    public static string ToWireName(this ScratchScope scope)
    {
        return scope switch
        {
            ScratchScope.Preview => "preview",
            ScratchScope.Draft => "draft",
            ScratchScope.Adaptation => "adaptation",
            ScratchScope.Run => "run",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scratch scope.")
        };
    }
}

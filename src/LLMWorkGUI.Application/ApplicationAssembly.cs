using System.Reflection;

namespace LLMWorkGUI.Application;

public static class ApplicationAssembly
{
    public static Assembly Instance { get; } = typeof(ApplicationAssembly).Assembly;
}

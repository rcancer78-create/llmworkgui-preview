using System.Reflection;

namespace LLMWorkGUI.Infrastructure;

public static class InfrastructureAssembly
{
    public static Assembly Instance { get; } = typeof(InfrastructureAssembly).Assembly;
}

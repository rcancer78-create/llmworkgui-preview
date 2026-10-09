using System.Reflection;

namespace LLMWorkGUI.Domain;

public static class DomainAssembly
{
    public static Assembly Instance { get; } = typeof(DomainAssembly).Assembly;
}

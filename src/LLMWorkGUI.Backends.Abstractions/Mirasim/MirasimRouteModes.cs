namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public static class MirasimRouteModes
{
    public const string ManualOnly = "ManualOnly";

    public const string Opaque = "Opaque";

    public static string Normalize(string? routeMode)
    {
        if (string.Equals(routeMode, Opaque, StringComparison.OrdinalIgnoreCase))
        {
            return Opaque;
        }

        return ManualOnly;
    }
}

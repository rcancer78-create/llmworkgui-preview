namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed class MirasimHealthStatus
{
    public const string SupportedVersion = "0.0.354";

    public bool Ok { get; init; }

    public string? Name { get; init; }

    public string? Version { get; init; }

    public int? Pid { get; init; }

    public string? InstanceId { get; init; }

    public long? Uptime { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSupportedVersion =>
        string.Equals(Version, SupportedVersion, StringComparison.OrdinalIgnoreCase);

    public static MirasimHealthStatus Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new MirasimHealthStatus
        {
            Ok = false,
            ErrorMessage = reason
        };
    }
}

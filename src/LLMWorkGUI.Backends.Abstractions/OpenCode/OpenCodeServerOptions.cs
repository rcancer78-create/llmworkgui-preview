namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public sealed class OpenCodeServerOptions
{
    public const string SectionName = "OpenCodeServer";

    public const string DefaultHostname = "127.0.0.1";

    public string Hostname { get; set; } = DefaultHostname;

    public int Port { get; set; }

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public string? CustomExecutablePath { get; set; }

    public static bool IsLoopbackHostname(string? hostname)
    {
        return string.Equals(hostname, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public void Validate()
    {
        if (!IsLoopbackHostname(Hostname))
        {
            throw new ArgumentException(
                $"Hostname '{Hostname}' is not allowed. Only loopback hostnames '127.0.0.1' or 'localhost' are permitted.",
                nameof(Hostname));
        }

        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Port),
                Port,
                "Port must be between 0 and 65535; 0 requests an OS-assigned port.");
        }

        if (StartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartupTimeout),
                StartupTimeout,
                "StartupTimeout must be positive.");
        }

        if (DisposeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DisposeTimeout),
                DisposeTimeout,
                "DisposeTimeout must be positive.");
        }

        if (CustomExecutablePath is not null && string.IsNullOrWhiteSpace(CustomExecutablePath))
        {
            throw new ArgumentException(
                "CustomExecutablePath must not be empty or whitespace when specified.",
                nameof(CustomExecutablePath));
        }
    }
}

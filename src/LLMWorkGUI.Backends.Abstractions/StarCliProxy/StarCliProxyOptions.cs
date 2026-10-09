namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Configuration for the managed star-cliproxy gateway (ТЗ §6.4, §6.11a, ADR-0007).
/// Defaults keep the gateway on loopback, pick a free port, and never place secrets on the
/// command line; proxy secrets are delivered through the owned process environment/secret store.
/// </summary>
public sealed class StarCliProxyOptions
{
    public const string SectionName = "StarCliProxy";

    public const string DefaultHostname = "127.0.0.1";

    public const string DefaultConfigFileName = "config.yaml";

    public const string ConfigPathPlaceholder = "{configPath}";

    public static readonly IReadOnlyList<string> DefaultStartArguments =
        ["--config", ConfigPathPlaceholder];

    /// <summary>Loopback hostname. Only <c>127.0.0.1</c> or <c>localhost</c> are permitted.</summary>
    public string Hostname { get; set; } = DefaultHostname;

    /// <summary>Loopback port; <c>0</c> requests a free OS-assigned port for each instance.</summary>
    public int Port { get; set; }

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Explicit star-cliproxy executable. When null the resolver checks the environment and PATH.</summary>
    public string? CustomExecutablePath { get; set; }

    public string ConfigFileName { get; set; } = DefaultConfigFileName;

    /// <summary>
    /// Arguments for the managed gateway. <see cref="ConfigPathPlaceholder"/> is replaced with the
    /// generated configuration file inside the run directory. Secrets are never placed here.
    /// </summary>
    public IList<string> StartArguments { get; set; } = new List<string>(DefaultStartArguments);

    /// <summary>
    /// Optional secret-store reference holding the proxy API key. The key itself is not a
    /// configuration value: the configuration binder has no property it can fill from appsettings.
    /// When this reference is absent or empty, the manager generates a key and stores it.
    /// </summary>
    public string? ApiKeySecretReference { get; set; }

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
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout), StartupTimeout, "StartupTimeout must be positive.");
        }

        if (HealthCheckTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthCheckTimeout), HealthCheckTimeout, "HealthCheckTimeout must be positive.");
        }

        if (DisposeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DisposeTimeout), DisposeTimeout, "DisposeTimeout must be positive.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), RequestTimeout, "RequestTimeout must be positive.");
        }

        if (CustomExecutablePath is not null && string.IsNullOrWhiteSpace(CustomExecutablePath))
        {
            throw new ArgumentException(
                "CustomExecutablePath must not be empty or whitespace when specified.",
                nameof(CustomExecutablePath));
        }

        if (string.IsNullOrWhiteSpace(ConfigFileName))
        {
            throw new ArgumentException("ConfigFileName must not be empty or whitespace.", nameof(ConfigFileName));
        }

        if (Path.IsPathRooted(ConfigFileName) || ConfigFileName is "." or ".." ||
            ConfigFileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
            ConfigFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "ConfigFileName must be a file name generated inside the run directory, not an absolute path.",
                nameof(ConfigFileName));
        }
    }
}

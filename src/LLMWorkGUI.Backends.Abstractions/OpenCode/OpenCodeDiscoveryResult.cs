namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public sealed record OpenCodeDiscoveryResult
{
    public required bool IsInstalled { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Version { get; init; }

    public required bool IsSupportedVersion { get; init; }

    public string? ErrorMessage { get; init; }

    public static OpenCodeDiscoveryResult NotInstalled(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new OpenCodeDiscoveryResult
        {
            IsInstalled = false,
            IsSupportedVersion = false,
            ErrorMessage = errorMessage
        };
    }

    public static OpenCodeDiscoveryResult Detected(string executablePath, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return new OpenCodeDiscoveryResult
        {
            IsInstalled = true,
            ExecutablePath = executablePath,
            Version = version,
            IsSupportedVersion = true
        };
    }

    public static OpenCodeDiscoveryResult Unsupported(string executablePath, string? version, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        return new OpenCodeDiscoveryResult
        {
            IsInstalled = true,
            ExecutablePath = executablePath,
            Version = version,
            IsSupportedVersion = false,
            ErrorMessage = errorMessage
        };
    }
}

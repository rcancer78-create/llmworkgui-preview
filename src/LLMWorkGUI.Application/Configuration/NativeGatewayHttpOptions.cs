namespace LLMWorkGUI.Application.Configuration;

/// <summary>Explicit local server configuration. Stores a secret reference, never a raw key.</summary>
public sealed class NativeGatewayHttpOptions
{
    public const string SectionName = "NativeGatewayHttp";
    public bool Enabled { get; set; }
    public int Port { get; set; } = 5157;
    public string? ProjectId { get; set; }
    public string? RootPath { get; set; }
    public string? ApiKeySecretReference { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 600;
}

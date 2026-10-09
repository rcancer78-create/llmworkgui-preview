namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record OpenCodeCreateSessionRequest
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? AdaptationAdmissionId { get; init; }

    public string? Directory { get; init; }

    public string? Title { get; init; }

    public string? Model { get; init; }

    public string? Agent { get; init; }

    /// <summary>Selects the managed OpenCode process for this provider profile. Not sent to the server.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProviderProfileId { get; init; }
}

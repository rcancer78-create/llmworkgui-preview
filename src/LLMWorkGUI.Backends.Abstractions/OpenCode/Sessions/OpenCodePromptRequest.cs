namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record OpenCodePromptRequest
{
    /// <summary>Local project authority checked after URI resolution, immediately before HTTP dispatch.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, OpenCodePromptRequest, Uri, CancellationToken, Task<bool>>? DispatchAuthorization { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? AdaptationAdmissionId { get; init; }

    public required string Prompt { get; init; }

    public string MessageId { get; init; } = "msg_" + Guid.NewGuid().ToString("N");

    public string? Model { get; init; }

    public string? Agent { get; init; }
}

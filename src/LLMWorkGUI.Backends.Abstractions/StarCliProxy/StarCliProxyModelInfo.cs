namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// One model alias exposed by the star-cliproxy model catalog (<c>GET /v1/models</c>).
/// The alias is what requests must send; the provider id identifies the backing CLI when reported.
/// </summary>
public sealed record StarCliProxyModelInfo(
    string Id,
    string? OwnedBy = null,
    string? ProviderId = null,
    DateTimeOffset? CreatedAtUtc = null);

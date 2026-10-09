using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Request for the ACP <c>session/prompt</c> call (ADR-0003 §4, <c>acp-prompt-exchange-sample.json</c>).
/// The prompt is sent as structured content blocks; the optional model string is the validated
/// combined form <c>&lt;baseModelId&gt;[&lt;overrides&gt;]</c> confirmed by per-model discovery.
/// <see cref="ClientRequestId"/> and <see cref="PromptHash"/> carry the execution identity required
/// by ТЗ §6.7; the same <see cref="ClientRequestId"/> is never retried automatically.
/// </summary>
public sealed record CursorAcpPromptRequest
{
    /// <summary>Native session identifier the turn belongs to (<c>params.sessionId</c>).</summary>
    public required string SessionId { get; init; }

    /// <summary>Prompt text sent as a single <c>text</c> content block (<c>params.prompt</c>).</summary>
    public required string Prompt { get; init; }

    /// <summary>Validated combined model string; omitted from the wire request when null.</summary>
    public string? Model { get; init; }

    /// <summary>Persisted-route sends require an explicit native set_model acknowledgement before prompt dispatch.
    /// False preserves the legacy direct protocol request shape; neither form proves terminal route identity.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RequireModelAcknowledgement { get; init; }

    /// <summary>
    /// Native mode to acknowledge through session/set_mode before dispatch. Supervised turns always
    /// supply the mode computed by the policy. Null is only for direct protocol callers deliberately
    /// retaining the agent's current mode; it does not establish a read-only capability.
    /// </summary>
    public string? ModeId { get; init; }

    /// <summary>Stops local preparation without cancelling the terminal response wait after dispatch.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationToken BeforeDispatchCancellationToken { get; init; }

    /// <summary>
    /// Local dispatch observation, not native delivery evidence. True means the transport prompt
    /// call was attempted; false means local preparation ended. The first observation wins.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<bool>? DispatchObserver { get; init; }

    /// <summary>Project-bound local authorization checked against this exact prepared request at the
    /// stdio write boundary. Transports without that boundary capability must refuse the request.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CursorAcpPromptRequest, CancellationToken, Task<bool>>? AuthorizeDispatchAsync { get; init; }

    /// <summary>UUID identity of the turn; an automatic retry of the same id is forbidden.</summary>
    public required string ClientRequestId { get; init; }

    /// <summary>Canonical hash of <see cref="Prompt"/> used for execution accounting.</summary>
    public required string PromptHash { get; init; }

    /// <summary>Creates a request with a fresh <see cref="ClientRequestId"/> and canonical hash.</summary>
    public static CursorAcpPromptRequest Create(
        string sessionId,
        string prompt,
        string? model = null,
        string? clientRequestId = null)
    {
        return new CursorAcpPromptRequest
        {
            SessionId = sessionId,
            Prompt = prompt,
            Model = model,
            ClientRequestId = string.IsNullOrWhiteSpace(clientRequestId)
                ? Guid.NewGuid().ToString("D")
                : clientRequestId,
            PromptHash = ComputePromptHash(prompt)
        };
    }

    /// <summary>Canonical SHA-256 hash of the UTF-8 prompt text, lowercase hexadecimal.</summary>
    public static string ComputePromptHash(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
    }
}

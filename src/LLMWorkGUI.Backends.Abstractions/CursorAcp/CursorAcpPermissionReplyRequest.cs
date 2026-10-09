namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Permission decision sent to the agent (<c>acp-permission-exchange-sample.json</c>). Only a
/// one-shot allow and a deny are representable: persistent rules and <c>allow for execution</c>
/// are excluded by construction (ADR-0003 §4.2, ТЗ §6.9).
/// </summary>
public enum CursorAcpPermissionDecision
{
    /// <summary>Allow exactly this one request (<c>allow_once</c>).</summary>
    AllowOnce,

    /// <summary>Deny the request (<c>deny</c>).</summary>
    Deny
}

/// <summary>
/// Reply to an ACP <c>session/request_permission</c> request. The reply is a JSON-RPC response
/// frame correlated to the permission request id; only <c>allow_once</c> and <c>deny</c> are
/// permitted decisions.
/// </summary>
public sealed record CursorAcpPermissionReplyRequest
{
    /// <summary>Permission request identifier to answer.</summary>
    public required string PermissionId { get; init; }

    /// <summary>One-shot decision; never a persistent rule.</summary>
    public required CursorAcpPermissionDecision Decision { get; init; }

    /// <summary>Native session the permission belongs to, when known.</summary>
    public string? SessionId { get; init; }
}

/// <summary>Classifies why a permission reply could not be written to the transport.</summary>
public enum CursorAcpPermissionReplyFailureKind
{
    /// <summary>The initialize handshake has not completed successfully.</summary>
    NotReady,

    /// <summary>The permission request identifier was empty.</summary>
    InvalidPermissionId,

    /// <summary>The JSON-RPC stdio transport failed while writing the response frame.</summary>
    TransportFailure
}

/// <summary>
/// Outcome of a permission reply. A permission reply is a response frame with no correlated
/// response; a successful write is the terminal evidence for the reply itself.
/// </summary>
public sealed record CursorAcpPermissionReplyResult
{
    private CursorAcpPermissionReplyResult(
        bool isSent,
        CursorAcpPermissionReplyFailureKind? failureKind,
        string? blocker,
        string? guidance)
    {
        IsSent = isSent;
        FailureKind = failureKind;
        Blocker = blocker;
        Guidance = guidance;
    }

    public bool IsSent { get; }

    public CursorAcpPermissionReplyFailureKind? FailureKind { get; }

    /// <summary>Exact degraded-mode reason; null when the reply was written.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the reply was written.</summary>
    public string? Guidance { get; }

    public static CursorAcpPermissionReplyResult Sent() =>
        new(isSent: true, failureKind: null, blocker: null, guidance: null);

    public static CursorAcpPermissionReplyResult Degraded(
        CursorAcpPermissionReplyFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpPermissionReplyResult(
            isSent: false,
            failureKind,
            blocker,
            guidance);
    }
}

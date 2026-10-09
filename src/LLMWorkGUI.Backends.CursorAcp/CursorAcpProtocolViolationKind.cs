namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>Classifies ACP initialize response violations detected by the handshake validator.</summary>
public enum CursorAcpProtocolViolationKind
{
    /// <summary>The initialize result did not match the expected ACP shape.</summary>
    MalformedResponse,

    /// <summary><c>protocolVersion</c> was missing, was not a JSON number, or was not 1.</summary>
    UnsupportedVersion,

    /// <summary>A capability required by ADR-0003 §2.3 was absent or false.</summary>
    MissingRequiredCapability
}

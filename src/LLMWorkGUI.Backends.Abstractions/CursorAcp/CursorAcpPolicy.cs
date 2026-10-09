namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Availability and degraded-mode policy constants for the native Cursor ACP backend
/// (ADR-0003, ТЗ §2.1, §6.2, §6.12). ACP over stdio is the only product transport; the CLI
/// print-mode fallback is diagnostic-only and is never used as a hidden replacement.
/// </summary>
public static class CursorAcpPolicy
{
    /// <summary>Executable name of the Cursor Agent CLI managed by the adapter.</summary>
    public const string ExecutableName = "cursor-agent";

    /// <summary>ACP subcommand that switches cursor-agent into the JSON-RPC stdio mode.</summary>
    public const string AcpArgument = "acp";

    /// <summary>
    /// Exact degraded-mode blocker reported when the Cursor Agent CLI cannot be located in PATH
    /// or in the standard Windows install locations. No CLI print-mode fallback is permitted.
    /// </summary>
    public const string NotInstalledBlocker =
        "The Cursor Agent CLI ('cursor-agent') was not found on PATH or in the standard Windows " +
        "install locations (configured CURSOR_AGENT_EXECUTABLE/CURSOR_AGENT_PATH, " +
        "%LOCALAPPDATA%\\Programs\\cursor, %LOCALAPPDATA%\\Programs\\cursor-agent, " +
        "%LOCALAPPDATA%\\cursor-agent, %APPDATA%\\npm, %USERPROFILE%\\.cursor\\bin).";

    /// <summary>
    /// User instruction attached to the not-installed degraded state. It explicitly states that
    /// no hidden CLI fallback replaces ACP (ADR-0003 §9, ТЗ §6.11, §6.12).
    /// </summary>
    public const string NotInstalledGuidance =
        "Install Cursor Agent and make 'cursor-agent' available on PATH (or configure " +
        "CURSOR_AGENT_EXECUTABLE), then retry. The Cursor backend stays in degraded mode; " +
        "no hidden cursor-agent --print fallback is used.";

    /// <summary>User instruction attached when the executable exists but its version cannot be probed.</summary>
    public const string VersionProbeGuidance =
        "Verify that 'cursor-agent --version' runs successfully for the current Windows user. " +
        "The Cursor backend stays in degraded mode until a supported Cursor Agent version is detected.";

    /// <summary>
    /// User instruction attached when the ACP handshake reports an unsupported protocol version or
    /// a missing required capability (ADR-0003 §2).
    /// </summary>
    public const string UnsupportedVersionGuidance =
        "Update Cursor Agent so that ACP 'initialize' reports the JSON number protocolVersion 1 with " +
        "the required capabilities (loadSession, promptCapabilities.image, sessionCapabilities.list, " +
        "mcpCapabilities.http/sse). No silent CLI print-mode fallback is performed.";

    /// <summary>User instruction attached when the ACP handshake itself fails.</summary>
    public const string HandshakeGuidance =
        "Retry the ACP handshake or restart the managed 'cursor-agent acp' process. The Cursor backend " +
        "stays in degraded mode until a successful initialize response is received.";

    /// <summary>User instruction attached when the managed 'cursor-agent acp' process cannot be started.</summary>
    public const string ProcessStartupGuidance =
        "Verify that Cursor Agent is installed, executable and not blocked by Windows policy, then retry. " +
        "The Cursor backend stays in degraded mode; ACP is never replaced by CLI print-mode.";

    /// <summary>User instruction attached when session/new is attempted before a ready handshake.</summary>
    public const string SessionNotReadyGuidance =
        "Complete the ACP initialize handshake successfully before creating a session, then retry. " +
        "The Cursor backend stays in degraded mode; no hidden cursor-agent --print fallback is used.";

    /// <summary>User instruction attached when the requested session working directory is rejected.</summary>
    public const string InvalidWorkingDirectoryGuidance =
        "Select an absolute workspace directory for the session; empty or relative paths are rejected " +
        "before any ACP call is sent.";

    /// <summary>User instruction attached when the ACP session/new exchange fails.</summary>
    public const string SessionRequestGuidance =
        "Retry session creation or restart the managed 'cursor-agent acp' process. The Cursor backend " +
        "stays in degraded mode until session/new returns a non-empty session id.";

    /// <summary>User instruction attached when session/load is attempted without the capability.</summary>
    public const string SessionLoadUnsupportedGuidance =
        "Update Cursor Agent so that ACP 'initialize' reports agentCapabilities.loadSession=true, then " +
        "retry the session restore. The persisted native session cannot be restored without it and no " +
        "hidden CLI print-mode fallback is used.";

    /// <summary>User instruction attached when the ACP session/load exchange fails.</summary>
    public const string SessionLoadGuidance =
        "Verify that the persisted native session still exists for this workspace, then retry the " +
        "restore or start a new session. The Cursor backend stays in degraded mode until session/load " +
        "returns a non-empty session id.";

    /// <summary>User instruction attached when the ACP session/prompt exchange fails.</summary>
    public const string PromptGuidance =
        "Retry the turn or restart the managed 'cursor-agent acp' process. An ambiguous outcome is " +
        "never retried automatically; a manual retry creates a new execution with a new client request id.";

    /// <summary>User instruction attached when the ACP session/cancel exchange fails.</summary>
    public const string CancelGuidance =
        "The in-band cancel could not be delivered. Wait for the turn watchdog or restart the managed " +
        "'cursor-agent acp' process; the writer lock is retained until terminal evidence exists.";

    /// <summary>User instruction attached when a permission reply cannot be delivered.</summary>
    public const string PermissionReplyGuidance =
        "Retry the permission reply or restart the managed 'cursor-agent acp' process. No automatic " +
        "approval is ever applied; only a one-shot allow or deny is permitted.";

    /// <summary>User instruction attached when the supervisor refuses a turn before dispatch.</summary>
    public const string TurnRefusedGuidance =
        "Resolve the reported mode, writer lock or concurrency blocker and start a new turn. The " +
        "refused turn was never sent to the agent.";
}

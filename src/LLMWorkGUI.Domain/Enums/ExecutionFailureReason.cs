namespace LLMWorkGUI.Domain.Enums;

public enum ExecutionFailureReason
{
    None,
    StartupFailure,
    AuthenticationFailure,
    ModelUnavailable,
    QuotaExceeded,
    NetworkTimeout,
    BufferOverflow,
    MalformedProtocol,
    WorkspaceConflict,
    ToolError,
    UserCancelled,
    InternalError
}

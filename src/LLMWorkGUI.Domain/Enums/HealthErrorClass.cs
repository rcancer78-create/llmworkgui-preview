namespace LLMWorkGUI.Domain.Enums;

public enum HealthErrorClass
{
    ExecutableMissingOrVersion,
    StartupOrSessionCreation,
    AuthenticationOrRefresh,
    QuotaOrRateLimit,
    NetworkOrTimeout,
    Provider4xx5xx,
    ModelUnavailableOrMismatch,
    MalformedProtocolEvent,
    ToolOrPermission,
    WorkspaceConflict,
    UserCancellation,
    UnknownOrAmbiguousCompletion,
    ShellCompositionOrQuoting,
    UnexpectedInteractiveInputWait,
    UserApprovalDeny
}

public static class HealthErrorClassPolicy
{
    public static bool IsAccountedByBreaker(this HealthErrorClass errorClass)
    {
        return errorClass is not (HealthErrorClass.UserCancellation
            or HealthErrorClass.UnknownOrAmbiguousCompletion
            or HealthErrorClass.UnexpectedInteractiveInputWait
            or HealthErrorClass.UserApprovalDeny);
    }
}

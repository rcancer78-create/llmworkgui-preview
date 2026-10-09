namespace LLMWorkGUI.Backends.OpenCode;

public static class OpenCodeApiPaths
{
    public const string Doc = "/doc";

    public const string GlobalHealth = "/global/health";

    public const string ApiHealth = "/api/health";

    public const string InstanceDispose = "/instance/dispose";

    public const string Event = "/event";

    public const string Sessions = "/session";

    public const string ConfiguredProviders = "/config/providers";

    public const string ApiProviders = "/api/provider";

    public const string Providers = "/provider";

    public const string ApiModels = "/api/model";

    public const string Models = "/model";

    public const string ProviderList = ApiProviders;

    public const string ModelList = ApiModels;

    public static string Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return $"/session/{Uri.EscapeDataString(sessionId)}";
    }

    public static string SessionFork(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return $"/session/{Uri.EscapeDataString(sessionId)}/fork";
    }

    public static string SessionAbort(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return $"/session/{Uri.EscapeDataString(sessionId)}/abort";
    }

    public static string SessionPromptAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return $"/session/{Uri.EscapeDataString(sessionId)}/prompt_async";
    }

    public static string PermissionReply(string permissionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);

        return $"/permission/{Uri.EscapeDataString(permissionId)}/reply";
    }

    public static string SessionEvent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // OpenCode exposes one instance bus at /event. Session scope is applied by the client.
        return Event;
    }
}

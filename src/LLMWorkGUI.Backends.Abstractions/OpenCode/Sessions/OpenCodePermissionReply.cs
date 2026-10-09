using System.Collections.Frozen;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record OpenCodePermissionReply
{
    public const string Once = "once";

    public const string Always = "always";

    public const string Reject = "reject";

    private static readonly IReadOnlySet<string> StandardResponseSet =
        new[] { Once, Always, Reject }.ToFrozenSet(StringComparer.Ordinal);

    public required string Response { get; init; }

    public bool Remember { get; init; }

    /// <summary>Local transport scope; never serialized into the native reply body.</summary>
    public string? ScopeSessionId { get; init; }

    public static IReadOnlySet<string> StandardResponses => StandardResponseSet;

    public bool IsStandard => StandardResponseSet.Contains(Response);

    public bool IsValid => !string.IsNullOrWhiteSpace(Response);

    public static OpenCodePermissionReply AllowOnce(bool remember = false)
    {
        return new OpenCodePermissionReply { Response = Once, Remember = remember };
    }

    public static OpenCodePermissionReply AllowAlways(bool remember = true)
    {
        return new OpenCodePermissionReply { Response = Always, Remember = remember };
    }

    public static OpenCodePermissionReply Deny()
    {
        return new OpenCodePermissionReply { Response = Reject };
    }
}

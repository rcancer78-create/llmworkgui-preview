namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// The stable prefixes a terminal <see cref="StarCliProxyStreamEventKind.Error"/> event carries.
/// <para>
/// An error event has no status field, so the only thing that separates "the gateway refused the request
/// with an HTTP status", "the turn was terminated because the reported route contradicted the requested
/// one" and "the body itself reported an error" is the prefix the producing client put there. Those three
/// are different facts for a caller: the first is a knowable non-delivery, the second is a route
/// substitution, and the third is whatever the body said. They are declared once here so the producer and
/// every consumer read the same vocabulary instead of each spelling the text out again.
/// </para>
/// </summary>
public static class StarCliProxyStreamEventMarkers
{
    /// <summary>
    /// The turn was terminated because observed evidence contradicted requested evidence, or contradicted
    /// evidence the turn had already reported. Terminal: no later chunk may complete it.
    /// </summary>
    public const string RouteMismatchPrefix = "star-cliproxy route mismatch:";

    /// <summary>
    /// The request was answered with a non-2xx status and produced no stream at all. The status and the
    /// bounded body excerpt follow the prefix.
    /// </summary>
    public const string HttpFailurePrefix = "star-cliproxy chat completion failed with HTTP ";

    /// <summary>Whether a terminal error message is the HTTP-refusal shape rather than a body error.</summary>
    public static bool IsHttpFailure(string? errorMessage) =>
        errorMessage is not null
        && errorMessage.StartsWith(HttpFailurePrefix, StringComparison.Ordinal);

    /// <summary>Whether a terminal error message is a route-substitution termination.</summary>
    public static bool IsRouteMismatch(string? errorMessage) =>
        errorMessage is not null
        && errorMessage.StartsWith(RouteMismatchPrefix, StringComparison.Ordinal);
}

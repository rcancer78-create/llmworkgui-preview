using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationValidationIssue
{
    public AdaptationValidationIssue(
        AdaptationBlockerKind kind,
        string? role,
        string message,
        bool isNotClearable = false)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown adaptation blocker kind.");
        }

        Kind = kind;
        Role = ApplicationGuard.OptionalNotBlank(role, nameof(role));
        Message = ApplicationGuard.NotBlank(message, nameof(message));
        IsNotClearable = isNotClearable;
    }

    public AdaptationBlockerKind Kind { get; }

    public string? Role { get; }

    public string Message { get; }

    /// <summary>
    /// True when the reported issue describes a gap in what could be verified rather than a difference the
    /// operator is being asked to accept — for example semantic content that the bounded analysis could not
    /// read. Such an issue stays a blocker for good: recording a decision for it would approve content the
    /// operator never saw, which is exactly what the acknowledgement gate exists to prevent.
    /// </summary>
    public bool IsNotClearable { get; }

    /// <summary>
    /// Stable identity of one reported issue: its kind, its role and the message that names the subject.
    /// Two issues of the same kind for different subjects therefore never share an identity, so a
    /// decision recorded for one blocker can never be replayed for another. Decisions are bound to this
    /// identity instead of to the blocker kind alone (fail closed on identity, not on kind).
    /// </summary>
    public string Identity => string.Concat(Kind.ToString(), "|",
        Role is null ? "N" : string.Concat("S", Role.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", Role),
        "|", Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", Message,
        "|", IsNotClearable ? "1" : "0");
}

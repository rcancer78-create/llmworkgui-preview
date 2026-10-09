namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// One semantic difference between the source package and the candidate package, reduced to the exact
/// evidence an operator has to decide on. The value identity of this record names the difference, so the
/// decision the activation gate matches is bound to one exact issue: a decision for one change never
/// covers another, and the change itself never carries or implies a consent.
/// </summary>
public sealed record SemanticChange
{
    public SemanticChange(
        SemanticChangeKind kind,
        string subject,
        string detail,
        bool nonClearable = false)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown semantic change kind.");
        }

        Kind = kind;
        Subject = ApplicationGuard.NotBlank(subject, nameof(subject));
        Detail = ApplicationGuard.NotBlank(detail, nameof(detail));
        NonClearable = nonClearable;
    }

    /// <summary>The semantic area the difference belongs to.</summary>
    public SemanticChangeKind Kind { get; }

    /// <summary>The file or manifest section the difference was observed in.</summary>
    public string Subject { get; }

    /// <summary>The bounded, explainable description of the difference. Never contains file content.</summary>
    public string Detail { get; }

    /// <summary>
    /// True when the comparison is inconclusive rather than positive: the bounded analysis could not read
    /// the evidence. Such a change is a gap in what was verified, so no operator decision, no confirmed
    /// issue list and no pre-send scope flag may suppress it.
    /// </summary>
    public bool NonClearable { get; }

    public override string ToString() =>
        string.Concat(Kind.ToString(), " | ", Subject, " | ", Detail);
}

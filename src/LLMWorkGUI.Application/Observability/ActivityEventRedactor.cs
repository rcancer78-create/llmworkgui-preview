namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// The single redaction boundary of the Activity Center.
/// <para>
/// Before this type the product redacted only what it *indexed*: <see cref="EventSearchIndex"/> ran the
/// redactor over the composed search text, but the service kept and handed out the original
/// <see cref="ActivityEvent"/> objects. A secret was therefore unfindable by a raw search and still
/// readable from a query result, a selected row, the detail pane and every diagnostic that printed the
/// event. Redacting at ingestion instead of at index time makes the leak structurally impossible: the
/// service never holds, indexes, returns, journals or binds an object that contains a raw secret, so
/// there is no second copy left to leak.
/// </para>
/// <para>
/// Structured identifiers are deliberately preserved verbatim - event id, execution id, session id,
/// route id, artifact name, artifact digest and size - because provenance is exactly what the Activity
/// Center exists to show, and none of them carries credential material by construction (the schema
/// already constrains secret references to a <c>urn:llmworkgui:secret:*</c> form).
/// </para>
/// </summary>
public sealed class ActivityEventRedactor
{
    private readonly Func<string, string> _redact;

    public ActivityEventRedactor(Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(redact);

        _redact = redact;
    }

    /// <summary>
    /// Returns an event whose operator-facing free text is redacted and whose provenance is untouched.
    /// An event that is already clean is returned unchanged, so a clean stream allocates no copies.
    /// </summary>
    public ActivityEvent Redact(ActivityEvent activityEvent)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        var title = RedactText(activityEvent.Title);
        var description = RedactText(activityEvent.Description);
        var diffText = activityEvent.DiffText is null ? null : RedactText(activityEvent.DiffText);
        var artifactContent = activityEvent.ArtifactContent is null ? null : RedactText(activityEvent.ArtifactContent);

        if (ReferenceEquals(title, activityEvent.Title)
            && ReferenceEquals(description, activityEvent.Description)
            && ReferenceEquals(diffText, activityEvent.DiffText)
            && ReferenceEquals(artifactContent, activityEvent.ArtifactContent))
        {
            return activityEvent;
        }

        return new ActivityEvent(
            activityEvent.Id,
            activityEvent.OccurredAtUtc,
            activityEvent.Kind,
            activityEvent.Role,
            activityEvent.State,
            activityEvent.Source,
            title,
            description,
            activityEvent.SessionId,
            activityEvent.ExecutionId,
            activityEvent.RouteId,
            diffText,
            activityEvent.ArtifactName,
            artifactContent,
            activityEvent.ArtifactSizeBytes,
            activityEvent.ArtifactSha256,
            activityEvent.ArtifactChangeStatus);
    }

    private string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = _redact(value);

        return string.IsNullOrEmpty(redacted) ? string.Empty : redacted;
    }
}

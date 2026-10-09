namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// Normalized event from a star-cliproxy SSE stream (ТЗ §6.11a). Raw JSON is preserved for
/// redacted diagnostics; typed fields cover content, reasoning, tool calls, usage and terminal
/// outcomes. Exit code, HTTP 2xx or a partial stream alone never mean success.
/// </summary>
public sealed record StarCliProxyStreamEvent
{
    public required StarCliProxyStreamEventKind Kind { get; init; }

    public string? Content { get; init; }

    public string? ToolName { get; init; }

    public string? ToolArgumentsJson { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public string? FinishReason { get; init; }

    public string? ErrorMessage { get; init; }

    public string? RawJson { get; init; }

    public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public StarCliProxyObservedEvidence Evidence { get; init; } = StarCliProxyObservedEvidence.Empty;

    public static StarCliProxyStreamEvent ContentDelta(
        string content,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null,
        string? rawJson = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.ContentDelta,
            Content = content,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty,
            RawJson = rawJson
        };

    public static StarCliProxyStreamEvent ReasoningDelta(
        string content,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null,
        string? rawJson = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.ReasoningDelta,
            Content = content,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty,
            RawJson = rawJson
        };

    public static StarCliProxyStreamEvent ToolCall(
        string toolName,
        string? toolArgumentsJson,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null,
        string? rawJson = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.ToolCall,
            ToolName = toolName,
            ToolArgumentsJson = toolArgumentsJson,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty,
            RawJson = rawJson
        };

    public static StarCliProxyStreamEvent Usage(
        int? promptTokens,
        int? completionTokens,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null,
        string? rawJson = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.Usage,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty,
            RawJson = rawJson
        };

    public static StarCliProxyStreamEvent Completed(
        string? finishReason,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.Completed,
            FinishReason = finishReason,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty
        };

    public static StarCliProxyStreamEvent Error(
        string errorMessage,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null,
        string? rawJson = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.Error,
            ErrorMessage = errorMessage,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty,
            RawJson = rawJson
        };

    public static StarCliProxyStreamEvent Malformed(
        string rawJson,
        DateTimeOffset receivedAtUtc,
        StarCliProxyObservedEvidence? evidence = null) =>
        new()
        {
            Kind = StarCliProxyStreamEventKind.Malformed,
            RawJson = rawJson,
            ReceivedAtUtc = receivedAtUtc,
            Evidence = evidence ?? StarCliProxyObservedEvidence.Empty
        };
}

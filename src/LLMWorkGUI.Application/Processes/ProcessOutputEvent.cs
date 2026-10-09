namespace LLMWorkGUI.Application.Processes;

public sealed record ProcessOutputEvent(
    ProcessStreamKind StreamKind,
    string Text,
    DateTimeOffset TimestampUtc,
    int BytesCount);

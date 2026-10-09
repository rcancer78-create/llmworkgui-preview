using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Repositories;

public sealed record ExecutionEventRecord(
    string Id,
    string ExecutionId,
    long Sequence,
    string EventKind,
    string? NormalizedRedactedPayloadJson,
    string? RawRedactedPayloadText,
    DataClassification? DataClassification,
    DateTimeOffset OccurredAt);

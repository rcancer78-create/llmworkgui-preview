namespace LLMWorkGUI.Domain.Enums;

public enum AdaptationBlockerKind
{
    MissingModel,
    MissingCapability,
    DisallowedSemanticChange,
    DetectedSecret,
    InvalidSchema,
    Other
}

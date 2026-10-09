namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// The seven versioned document templates of the standard development chain (ROADMAP Phase 10E):
/// problem statement, architecture, technical specification, roadmap, task/fix packet, review report and
/// acceptance report. Every kind carries its own required sections and completeness criteria.
/// </summary>
public enum DocumentTemplateKind
{
    ProblemStatement = 0,
    Architecture = 1,
    TechnicalSpecification = 2,
    Roadmap = 3,
    TaskPacket = 4,
    ReviewReport = 5,
    AcceptanceReport = 6
}

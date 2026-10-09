namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Summary of one saved AGY profile reported by <c>agy-profile list</c> (ТЗ §6.4, §6.11a).
/// The profile name is the sanitized, credentials-free identifier; tokens are never exposed.
/// </summary>
public sealed record AgyProfileSummary(string Name, bool IsActive);

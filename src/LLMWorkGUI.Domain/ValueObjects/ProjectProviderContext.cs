namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>Local identifiers to re-read from storage, never a grant or a remote payload.</summary>
public sealed record ProjectProviderContext(string ProjectId, string ProviderProfileId);

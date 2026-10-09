namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Descriptive authentication method reported by ACP <c>initialize</c>. The adapter surfaces it to
/// the UI only; Cursor credentials are never extracted or stored (ADR-0003 §2.4, ТЗ §6.12).
/// </summary>
public sealed record CursorAcpAuthMethod
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }
}

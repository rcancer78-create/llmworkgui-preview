namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Snapshot of the Cursor ACP model discovery recorded in
/// <c>docs/protocols/cursor/acp-model-catalog.json</c> (ADR-0003 §7.1). The catalog is
/// deserialized from the sanitized discovery snapshot; this baseline does not invent or send any
/// <c>models/list</c> RPC over ACP stdio.
/// </summary>
public sealed record CursorAcpModelCatalog
{
    /// <summary>Version of the discovery snapshot schema.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Protocol identifier reported by the snapshot; must be <c>cursor-agent-acp</c>.</summary>
    public required string Protocol { get; init; }

    /// <summary>Cursor Agent version the discovery snapshot was recorded against.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>Discovered models with their per-model parameterized overrides.</summary>
    public required IReadOnlyList<CursorAcpModelInfo> Models { get; init; }

    /// <summary>Finds a discovered model by its exact base model id (ordinal comparison).</summary>
    public CursorAcpModelInfo? FindModel(string baseModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseModelId);

        foreach (var model in Models)
        {
            if (string.Equals(model.ModelId, baseModelId, StringComparison.Ordinal))
            {
                return model;
            }
        }

        return null;
    }
}

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Deserializes the Cursor ACP model discovery snapshot (<c>acp-model-catalog.json</c>,
/// ADR-0003 §7.1). This baseline reads and validates the sanitized snapshot only: no network or
/// ACP RPC call is issued for model discovery (AC1).
/// </summary>
public interface ICursorAcpModelCatalogService
{
    /// <summary>Parses the catalog JSON strictly; malformed or incomplete snapshots are rejected.</summary>
    CursorAcpModelCatalog Deserialize(string json);

    /// <summary>Reads and parses the catalog snapshot from a file.</summary>
    CursorAcpModelCatalog LoadFromFile(string path);
}

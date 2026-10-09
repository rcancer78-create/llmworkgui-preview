namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Strict grammar and fail-closed validation of parameterized model overrides
/// <c>&lt;baseModelId&gt;[&lt;parameter&gt;=&lt;value&gt;,...]</c> (ADR-0003 §7, AC2/AC3). The
/// authority for validation is the per-model <c>overrides[]</c> discovery of the selected model,
/// never the root <c>parameterStates</c> table.
/// </summary>
public interface ICursorAcpModelSelector
{
    /// <summary>
    /// Parses the wire form and validates it against the catalog. Rejects malformed syntax,
    /// duplicate parameters and anything not confirmed by per-model discovery.
    /// </summary>
    CursorAcpModelSelection Parse(string selection, CursorAcpModelCatalog catalog);

    /// <summary>Validates a base id plus override set against the per-model discovery.</summary>
    CursorAcpModelSelection Validate(
        string baseModelId,
        IReadOnlyList<CursorAcpOverrideValue> overrides,
        CursorAcpModelCatalog catalog);

    /// <summary>
    /// Validates and formats a selection for the wire. Parameters are emitted in deterministic
    /// ordinal order; an empty override set yields the plain base model id without brackets.
    /// </summary>
    string Format(CursorAcpModelSelection selection, CursorAcpModelCatalog catalog);

    /// <summary>Validates and formats a base id plus override set for the wire.</summary>
    string Format(
        string baseModelId,
        IReadOnlyList<CursorAcpOverrideValue> overrides,
        CursorAcpModelCatalog catalog);
}

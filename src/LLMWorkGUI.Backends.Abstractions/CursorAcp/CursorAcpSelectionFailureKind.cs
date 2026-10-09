namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies why a Cursor ACP model selection was rejected.</summary>
public enum CursorAcpSelectionFailureKind
{
    /// <summary>The text did not match <c>&lt;baseModelId&gt;[&lt;parameter&gt;=&lt;value&gt;,...]</c>.</summary>
    MalformedSyntax,

    /// <summary>The base model id is not present in the discovered model catalog.</summary>
    UnknownModel,

    /// <summary>The parameter is not declared in the model's per-model discovery.</summary>
    UnknownParameter,

    /// <summary>The parameter exists but its capability state is not Supported.</summary>
    UnsupportedParameter,

    /// <summary>The value is not in the parameter's discovered value list.</summary>
    InvalidValue,

    /// <summary>The same parameter appears more than once.</summary>
    DuplicateParameter
}

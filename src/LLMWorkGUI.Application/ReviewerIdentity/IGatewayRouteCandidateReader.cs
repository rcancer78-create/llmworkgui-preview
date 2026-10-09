namespace LLMWorkGUI.Application.ReviewerIdentity;

/// <summary>
/// Reads the persisted library as the diagnostic resolver sees it: every <c>Routes</c> row, the count of
/// all of them, and the provider profile, account and model each one names.
/// <para>
/// Read-only, and deliberately not registered anywhere. Nothing in this build composes a caller for it,
/// which is the mechanical half of "this is not a dispatch decision": the resolver has no route from any
/// channel, executor, run service or composition root to a route id, so the review gate cannot be opened by
/// a future edit that wires this in without that edit also being visible as a wiring change.
/// </para>
/// <para>
/// The reader interprets only persisted text and refuses text it does not recognise, naming the table and
/// the column. A row whose backend or health value this build does not know is not a row it can answer for,
/// and defaulting it - the way <c>Routes.Backend</c> once defaulted to OpenCode - would silently re-point it
/// at a backend nobody chose.
/// </para>
/// </summary>
public interface IGatewayRouteCandidateReader
{
    /// <summary>
    /// The current persisted library, or a refusal at read time. A row whose persisted backend or health text
    /// is not a value this build knows raises <see cref="InvalidDataException"/> rather than returning a
    /// partial answer.
    /// </summary>
    Task<GatewayRouteCandidateSnapshot> ReadCandidatesAsync(CancellationToken cancellationToken = default);
}

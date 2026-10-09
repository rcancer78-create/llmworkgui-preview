namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Extensible adapter-route/model invocation contract for the adaptation engine. Implementations may
/// call a live backend or return deterministic offline responses; the engine never invokes a model
/// on its own (explicit user command only).
/// </summary>
public interface IAdaptationModelInvoker
{
    Task<AdaptationModelResponse> InvokeModelAsync(
        AdaptationModelRequest request,
        CancellationToken cancellationToken = default);
}

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// Separates the pre-coder gate checks so each blocking condition is reported on its own (ROADMAP Phase
/// 10E). The coder must not start without a unanimous <c>Approve</c> of every required reviewer on the
/// current hash of every required document and a separate user approval.
/// Evaluation checks the supplied snapshot only; execution services must establish their own persisted
/// artifact, review-response and route authority before authorizing a production transition.
/// </summary>
public interface IPreCoderGateValidator
{
    PreCoderGateValidationResult Evaluate(PreCoderGateRequest request);
}

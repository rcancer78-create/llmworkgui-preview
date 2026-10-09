namespace LLMWorkGUI.Application.Processes;

/// <summary>
/// Optional metadata-only companion to an output progress observer. A capture failure does
/// not stop pipe drainage or prove physical termination. Callbacks must return promptly;
/// a host requiring complete spools may cancel its owned process and join normal cleanup.
/// </summary>
public interface IProcessOutputCaptureObserver
{
    void OnOutputCaptureFailure(ProcessStreamKind streamKind);
}

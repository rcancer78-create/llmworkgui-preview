namespace LLMWorkGUI.App.Services;

/// <summary>Thin clipboard abstraction so view models stay testable without a real desktop clipboard.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

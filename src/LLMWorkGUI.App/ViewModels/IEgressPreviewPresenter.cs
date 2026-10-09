using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>Common human confirmation of every displayed fragment; null means cancellation.</summary>
public interface IEgressPreviewPresenter
{
    Task<IReadOnlyList<string>?> ConfirmAsync(EgressPreview preview, string destinationDisplay, CancellationToken cancellationToken);
}

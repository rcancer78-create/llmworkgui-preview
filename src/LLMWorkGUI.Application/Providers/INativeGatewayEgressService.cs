using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Application.Providers;

/// <summary>Exact fragment preview for the existing native composer; no native account/store I/O.</summary>
public interface INativeGatewayEgressService
{
    Task<EgressPreview> PrepareAsync(NativeGatewayTurnRequest request, CancellationToken cancellationToken = default);
    void ApproveFragment(Guid previewId, string fragmentId, string displayedContentSha256);
    void Revoke(Guid previewId);
}

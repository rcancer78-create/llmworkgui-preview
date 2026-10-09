namespace LLMWorkGUI.Application.Workflows;

public interface IScratchWorkspaceManager
{
    Task<int> RecoverAbandonedAdaptationWorkspacesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    Task<ScratchWorkspace> CreateWorkspaceAsync(
        ScratchScope scope,
        string scopeId,
        CancellationToken cancellationToken = default);

    Task ExtractBlobToWorkspaceAsync(
        string blobId,
        ScratchWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task PostOperationSourceHashCheckAsync(
        string blobId,
        CancellationToken cancellationToken = default);
}

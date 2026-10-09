namespace LLMWorkGUI.Application.Workflows;

public interface IWorkflowManifestParser
{
    Task<WorkflowManifest> ParseManifestAsync(
        Stream archiveStream,
        CancellationToken cancellationToken = default);
}

using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class DocumentPreviewConcurrencyTests
{
    [Fact]
    public async Task EditingDuringSecretScan_CannotExposeUnscannedContentAsCleanPreview()
    {
        var scanner = new PausedScanner();
        var documents = new DocumentTemplateService(scanner);
        var draft = await documents.GenerateDraftAsync(DocumentTemplateKind.ProblemStatement, "Synthetic", "safe content");
        var preview = documents.GeneratePreSendPreviewAsync(draft.DraftId);
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("safe content", scanner.ScannedContent);
        await documents.UpdateDraftAsync(draft.DraftId, "new unscanned synthetic credential");
        scanner.Complete.SetResult(WorkflowSecretScanReport.Empty);

        await Assert.ThrowsAsync<WorkflowValidationException>(() => preview);
    }

    private sealed class PausedScanner : IWorkflowSecretScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkflowSecretScanReport> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ScannedContent { get; private set; }

        public Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken = default)
        {
            ScannedContent = System.Text.Encoding.UTF8.GetString(Assert.Single(files).Value);
            Started.SetResult();
            return Complete.Task;
        }

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

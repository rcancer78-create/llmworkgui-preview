using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowAdaptationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableIncludedContentAfterSuccessfulScanRefusesBeforeModel(bool start)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "owned readable workflow body")));
        using var scanner = new HoldReadAfterScan();
        var service = CreateContentAdmissionService(scanner);
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        try
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                if (start) await service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
                else await service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced);
            });
            Assert.Empty(_modelInvoker.Requests);
            Assert.IsType<WorkflowValidationException>(failure);
            Assert.Equal(1, scanner.SuccessfulScans);
        }
        finally
        {
            scanner.Dispose();
            if (scanner.HeldWorkspace is { } workspace) await workspace.CleanupWorkspaceAsync();
        }
        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("aggregate")]
    [InlineData("escaped")]
    public async Task OversizedSerializedSourceContextIsRefusedInPreviewBeforeModel(string shape)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var bodies = shape switch
        {
            "single" => new[] { new string('x', 210_000) },
            "aggregate" => new[] { new string('x', 110_000), new string('y', 110_000) },
            _ => new[] { new string('"', 80_000) }
        };
        // Exact native JSON serializer proves that escaping, not only source bytes, consumes the wire cap.
        var serialized = OpenCodeSessionJson.SerializePromptRequest(new OpenCodePromptRequest
        {
            Prompt = string.Join("\n", bodies), Model = NativeModelId, Agent = "plan"
        });
        Assert.True(Encoding.UTF8.GetByteCount(serialized) > SqliteAdaptationTransportPolicy.MaxWireBytes);
        var raw = ContextAdmissionArchive(bodies);
        var (_, version) = await SeedWorkflowAsync("version-1", raw);

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced));

        Assert.Empty(_modelInvoker.Requests);
        Assert.True(await _blobStore.VerifyBlobAsync(version.BlobId));
    }

    [Fact]
    public async Task OversizedFollowUpAndRetainedHistoryCannotReachTheSecondModelCall()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "owned context")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var session = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        try
        {
            await Assert.ThrowsAsync<WorkflowValidationException>(() => _service.SubmitFollowUpTurnAsync(
                new AdaptationFollowUpRequest(session.SessionId, new string('x', 210_000))));
            Assert.Single(_modelInvoker.Requests);
        }
        finally { await _service.DiscardSessionAsync(session.SessionId); }
    }

    private static byte[] ContextAdmissionArchive(IReadOnlyList<string> bodies) =>
        WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            for (var index = 0; index < bodies.Count; index++)
            {
                var entry = archive.CreateEntry($"context-{index}.md", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(bodies[index]));
            }
        });

    private WorkflowAdaptationService CreateContentAdmissionService(IWorkflowSecretScanner scanner) => new(
        _versionRepository, _packageRepository, _scratchManager, scanner, new WorkflowAdaptationPromptBuilder(),
        new ChatCapabilityFixtureCatalog(new SanitizedCatalogProvider(_providerRepository, _accountRepository, _healthStateRepository)),
        _quotaSnapshotRepository, _accountRepository, _modelInvoker, new AdaptationResponseParser(),
        new AdaptationReferenceValidator(), new SemanticDiffEngine(), new WorkflowDiffService(), _blobStore);

    private sealed class HoldReadAfterScan : IWorkflowSecretScanner, IDisposable
    {
        private readonly WorkflowSecretScanner _inner = new(new SensitiveDataFilter());
        private FileStream? _held;
        public int SuccessfulScans;
        public ScratchWorkspace? HeldWorkspace;
        public async Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace,
            CancellationToken token = default)
        {
            var result = await _inner.ScanScratchWorkspaceAsync(workspace, token);
            SuccessfulScans++;
            HeldWorkspace = workspace;
            _held = new FileStream(Path.Combine(workspace.DirectoryPath, "README.md"),
                FileMode.Open, FileAccess.Read, FileShare.None);
            return result;
        }
        public Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files,
            CancellationToken token = default) => _inner.ScanFilesAsync(files, token);
        public void Dispose() { _held?.Dispose(); _held = null; }
    }
}

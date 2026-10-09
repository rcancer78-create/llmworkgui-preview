using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class SemanticReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "semantic-review-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsharedUnreadableManifestCannotBeAcknowledged(bool removed)
    {
        var empty = Path.Combine(_root, "empty");
        var invalid = Path.Combine(_root, "invalid");
        Directory.CreateDirectory(empty); Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "workflow.json"), "{malformed");
        var changes = SemanticPackageComparison.Compare(SemanticPackageAnalyzer.Read(removed ? invalid : empty),
            SemanticPackageAnalyzer.Read(removed ? empty : invalid));
        Assert.Contains(changes, change => change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootManifestSharesTheActualPackageByteBudget(bool binary)
    {
        Directory.CreateDirectory(_root);
        var content = new byte[SemanticPackageAnalyzer.MaxAnalyzedFileBytes];
        Array.Fill(content, (byte)'x');
        if (binary) content[0] = 0;
        for (var i = 0; i < 8; i++) File.WriteAllBytes(Path.Combine(_root, $"stage-{i}.md"), content);
        File.WriteAllText(Path.Combine(_root, "workflow.json"), "{\"roles\":[\"Executor\"]}");
        var facts = SemanticPackageAnalyzer.Read(_root);
        Assert.False(Assert.Single(facts.Manifests).Readable);
        Assert.Contains(SemanticPackageComparison.Compare(facts, facts),
            change => change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable);
    }

    [Fact]
    public async Task IncidentalSectionNamesDoNotCountAsDocumentHeadings()
    {
        var service = new DocumentTemplateService();
        var required = service.GetRequiredTemplate(DocumentTemplateKind.Roadmap).RequiredSections;
        var draft = await service.GenerateDraftAsync(DocumentTemplateKind.Roadmap, "Draft",
            "This paragraph mentions " + string.Join(", ", required) + " without defining those sections.");
        Assert.False(service.EvaluateCompleteness(draft.DraftId).AllRequiredSectionsPresent);
        Assert.Equal(required.Count, service.EvaluateCompleteness(draft.DraftId).MissingSections.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("line")]
    [InlineData("path")]
    public async Task UnmappableSecretReportCannotReturnFlaggedDraftText(string fault)
    {
        const string secret = "synthetic-preview-private-value-0123456789";
        var findings = fault == "empty" ? Array.Empty<WorkflowSecretFinding>() :
            new[] { new WorkflowSecretFinding(fault == "path" ? "unrelated.md" : "drafts/Roadmap.md",
                fault == "line" ? 99 : 1, "Fixture", "[redacted]") };
        var service = new DocumentTemplateService(new Scanner(new(true, findings, [])));
        var draft = await service.GenerateDraftAsync(DocumentTemplateKind.Roadmap, "Draft", "Public introduction\n" + secret);
        var preview = await service.GeneratePreSendPreviewAsync(draft.DraftId);
        Assert.True(preview.IsBlocked);
        Assert.DoesNotContain(secret, preview.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedOrRemovedDocumentConsentMustNameExactBytes(bool removed)
    {
        var empty = new SemanticPackageFacts();
        var first = new SemanticPackageFacts { DocumentDigests = new Dictionary<string, string> { ["README.md"] = new string('a', 64) } };
        var changed = new SemanticPackageFacts { DocumentDigests = new Dictionary<string, string> { ["README.md"] = new string('b', 64) } };
        var approved = Assert.Single(SemanticPackageComparison.Compare(removed ? first : empty, removed ? empty : first));
        var current = Assert.Single(SemanticPackageComparison.Compare(removed ? changed : empty, removed ? empty : changed));
        Assert.NotEqual(approved.Detail, current.Detail);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("entrypoints")]
    public void SemanticFindingDoesNotCopyUntrustedManifestNames(string section)
    {
        const string value = "token=synthetic-private-manifest-name-0123456789";
        var source = Path.Combine(_root, "source"); var candidate = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(source); Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(source, "workflow.json"), "{\"" + section + "\":[]}");
        File.WriteAllText(Path.Combine(candidate, "workflow.json"),
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string[]> { [section] = [value] }));
        var changes = SemanticPackageComparison.Compare(SemanticPackageAnalyzer.Read(source), SemanticPackageAnalyzer.Read(candidate));
        Assert.NotEmpty(changes);
        Assert.All(changes, change => Assert.DoesNotContain(value, change.Detail, StringComparison.Ordinal));
    }

    private sealed class Scanner(WorkflowSecretScanReport report) : IWorkflowSecretScanner
    {
        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace,
            CancellationToken token = default) => throw new NotSupportedException();
        public Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files,
            CancellationToken token = default) => Task.FromResult(report);
    }
}

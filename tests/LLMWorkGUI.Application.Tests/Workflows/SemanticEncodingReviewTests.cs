using System.Text;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class SemanticEncodingReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "semantic-encoding-review-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InvalidUtf8ManifestCannotProduceAcknowledgeableSemanticEvidence(bool invalidSource, bool invalidCandidate)
    {
        var source = CreatePackage("source", invalidSource);
        var candidate = CreatePackage("candidate", invalidCandidate);
        var sourceFacts = SemanticPackageAnalyzer.Read(source);
        var candidateFacts = SemanticPackageAnalyzer.Read(candidate);
        Assert.Equal(!invalidSource, Assert.Single(sourceFacts.Manifests).Readable);
        Assert.Equal(!invalidCandidate, Assert.Single(candidateFacts.Manifests).Readable);
        Assert.Contains(SemanticPackageComparison.Compare(sourceFacts, candidateFacts),
            change => change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable);
    }

    [Fact]
    public void CaseOnlyRenamedManifestStillReportsActualSemanticChange()
    {
        var source = CreatePackage("source", false);
        var candidate = CreatePackage("candidate", false);
        File.Move(Path.Combine(candidate, "workflow.json"), Path.Combine(candidate, "WORKFLOW.JSON"));
        File.WriteAllText(Path.Combine(candidate, "WORKFLOW.JSON"), "{\"stages\":[\"changed\"]}");
        Assert.Contains(SemanticPackageComparison.Compare(SemanticPackageAnalyzer.Read(source),
            SemanticPackageAnalyzer.Read(candidate)), change => change.Kind == SemanticChangeKind.ManifestStructure);
    }

    private string CreatePackage(string name, bool invalid)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes("{\"stages\":[\"ordinary\"]}");
        if (invalid) bytes[13] = 0xFF; // Inside a JSON string: replacement decoding used to conceal invalid UTF-8.
        File.WriteAllBytes(Path.Combine(directory, "workflow.json"), bytes);
        return directory;
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "semantic-encoding-review-", target, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}

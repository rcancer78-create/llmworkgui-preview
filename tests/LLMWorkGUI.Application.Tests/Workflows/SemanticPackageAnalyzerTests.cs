using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// Evidence for the bounded, explainable extraction that the ТЗ §6.14 semantic guard now decides on. The
/// tests read real package directories so the classifier, the manifest tier and the bounded limits are
/// verified as shipped rather than against hand-built fact objects.
/// </summary>
public sealed class SemanticPackageAnalyzerTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumerationAtExactEntryBudgetIsCompleteUnlessAnAdditionalEntryExists(bool additionalEntry)
    {
        Directory.CreateDirectory(_root);
        var count = SemanticPackageAnalyzer.MaxEnumeratedEntries + (additionalEntry ? 1 : 0);
        for (var index = 0; index < count; index++)
            Directory.CreateDirectory(Path.Combine(_root, $"empty-{index:D4}"));
        var facts = SemanticPackageAnalyzer.Read(_root);
        Assert.True(facts.InspectionSucceeded);
        Assert.Empty(facts.DocumentDigests);
        Assert.Empty(facts.Manifests);
        Assert.Empty(facts.UnverifiableFiles);
        Assert.Equal(additionalEntry, facts.AnalysisTruncated);
        Assert.Equal(additionalEntry, SemanticPackageComparison.Compare(facts, facts).Any(
            change => change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable));
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "llmworkgui-semantic-package-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("WORKFLOW.md", true)]
    [InlineData("docs/architecture.md", true)]
    [InlineData("prompts/executor.md", true)]
    [InlineData("prompts/nested/notes.txt", true)]
    [InlineData("stages/build.md", true)]
    [InlineData("quality/gates.md", true)]
    [InlineData("escalation/paths.md", true)]
    [InlineData("agents/reviewer.md", true)]
    [InlineData("roles/escalation.md", true)]
    [InlineData("scripts/run.ps1", false)]
    [InlineData("config/app.json", false)]
    [InlineData("config/secrets.env", false)]
    [InlineData("assets/logo.bin", false)]
    [InlineData("assets/logo.md", true)]
    [InlineData("notes.txt", false)]
    public void IsSemanticBearingFile_ClassifiesByPathAndExtension(string relativePath, bool expected)
    {
        Assert.Equal(expected, SemanticPackageAnalyzer.IsSemanticBearingFile(relativePath));
    }

    [Fact]
    public void Read_EmptyPackage_ReportsNoManifestAndNoDocuments()
    {
        Directory.CreateDirectory(_root);

        var facts = SemanticPackageAnalyzer.Read(_root);

        Assert.Empty(facts.Manifests);
        Assert.Empty(facts.DocumentDigests);
        Assert.Empty(facts.UnverifiableFiles);
        Assert.False(facts.AnalysisTruncated);
    }

    [Fact]
    public void Read_MissingDirectory_ReportsAnUnavailablePackage()
    {
        Assert.Same(SemanticPackageFacts.Empty, SemanticPackageAnalyzer.Read(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void Compare_TwoUnavailablePackagesCannotMasqueradeAsIdenticalEmptyPackages()
    {
        var source = SemanticPackageAnalyzer.Read(Path.Combine(_root, "missing-source"));
        var candidate = SemanticPackageAnalyzer.Read(Path.Combine(_root, "missing-candidate"));

        var changes = SemanticPackageComparison.Compare(source, candidate);

        Assert.NotEmpty(changes);
        Assert.All(changes, change =>
        {
            Assert.Equal(SemanticChangeKind.UnverifiableContent, change.Kind);
            Assert.True(change.NonClearable);
        });
        var result = new SemanticDiffEngine().Compare(new SemanticDiffRequest(null, [], true, source, candidate));
        Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, issue => Assert.True(issue.IsNotClearable));
    }

    [Fact]
    public void Read_EnumerationStopsAtBoundedPrefixWithoutWalkingTheUnreportedTail()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "stage.md");
        File.WriteAllText(file, "stage");
        var visited = 0;
        var maximumVisited = SemanticPackageAnalyzer.MaxEnumeratedEntries + 1;
        IEnumerable<FileSystemInfo> Entries(string _, EnumerationOptions options)
        {
            while (++visited <= maximumVisited)
                yield return new FileInfo(file);
            throw new InvalidOperationException("Enumeration continued into the unbounded tail.");
        }

        SemanticPackageFacts? facts = null;
        var error = Record.Exception(() => facts = SemanticPackageAnalyzer.Read(_root, Entries));

        Assert.Null(error);
        Assert.True(facts!.AnalysisTruncated);
        Assert.InRange(visited, SemanticPackageAnalyzer.MaxAnalyzedFiles + 1, maximumVisited);
        Assert.Contains(SemanticPackageComparison.Compare(facts, facts), change =>
            change.Kind == SemanticChangeKind.UnverifiableContent && change.Subject == "(package)" && change.NonClearable);
    }

    [Fact]
    public void Read_InaccessibleSubdirectoryIsRecordedInsteadOfSilentlyOmitted()
    {
        var restricted = Directory.CreateDirectory(Path.Combine(_root, "restricted"));
        IEnumerable<FileSystemInfo> Entries(string path, EnumerationOptions options)
        {
            if (Path.GetFullPath(path) == Path.GetFullPath(_root))
                return new FileSystemInfo[] { restricted };
            throw new UnauthorizedAccessException("Synthetic access boundary; no machine ACL mutation.");
        }

        var facts = SemanticPackageAnalyzer.Read(_root, Entries);

        Assert.Contains("restricted", facts.UnverifiableFiles);
        Assert.Contains(SemanticPackageComparison.Compare(facts, facts), change =>
            change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable);
    }

    [Theory]
    [InlineData("declaredRoles", "CustomRole", "customRole", SemanticChangeKind.DeclaredRoleSet)]
    [InlineData("roles", "CustomRole", "customRole", SemanticChangeKind.DeclaredRoleSet)]
    [InlineData("entrypoints", "Run.sh", "run.sh", SemanticChangeKind.EntrypointSet)]
    public void Read_CaseOnlyDeclaredNameChangesRemainSemantic(string section, string before, string after, SemanticChangeKind expected)
    {
        var source = ReadPackage(("workflow.json", $$"""{"{{section}}":["{{before}}"]}"""));
        var candidate = ReadPackage(("workflow.json", $$"""{"{{section}}":["{{after}}"]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));
        Assert.Equal(expected, change.Kind);
        Assert.DoesNotContain(before, change.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(after, change.Detail, StringComparison.Ordinal);
        Assert.Contains("added: 1 name(s), sha256:", change.Detail, StringComparison.Ordinal);
        Assert.Contains("removed: 1 name(s), sha256:", change.Detail, StringComparison.Ordinal);
        var unchanged = Assert.Single(SemanticPackageComparison.Compare(source,
            ReadPackage(("workflow.json", $$"""{"{{section}}":["different"]}"""))));
        Assert.NotEqual(unchanged.Detail, change.Detail);
    }

    [Theory]
    [InlineData("declaredRoles", SemanticChangeKind.DeclaredRoleSet)]
    [InlineData("entrypoints", SemanticChangeKind.EntrypointSet)]
    public void DeclaredNameAcknowledgementIsBoundToBothStructureAndUnchangedNames(string section, SemanticChangeKind kind)
    {
        SemanticChange Change(string retainedName, string stage)
        {
            var source = ReadPackage(("workflow.json", $$"""{"{{section}}":["{{retainedName}}"],"stages":["{{stage}}"]}"""));
            var candidate = ReadPackage(("workflow.json", $$"""{"{{section}}":["{{retainedName}}","added"],"stages":["{{stage}}"]}"""));
            return Assert.Single(SemanticPackageComparison.Compare(source, candidate).Where(change => change.Kind == kind));
        }

        var first = Change("retained-first", "build");
        var changedStructure = Change("retained-first", "release");
        var changedRetainedName = Change("retained-second", "build");
        Assert.NotEqual(first.Detail, changedStructure.Detail);
        Assert.NotEqual(first.Detail, changedRetainedName.Detail);
        Assert.Contains("source sha256:", first.Detail, StringComparison.Ordinal);
        Assert.Contains("candidate sha256:", first.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsContentOutOfTheFacts()
    {
        var facts = ReadPackage(("prompts/executor.md", "You are the executor and must never skip the gate."));

        var digest = Assert.Single(facts.DocumentDigests);

        Assert.Equal("prompts/executor.md", digest.Key);
        Assert.Equal(64, digest.Value.Length);
        Assert.DoesNotContain("executor", digest.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_StageOnlyChange_IsDetectedFromTheRealFiles()
    {
        var source = ReadPackage(("stages/build.md", "Stage 1: build."));
        var candidate = ReadPackage(("stages/build.md", "Stage 1: build. Stage 2: test."));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.SemanticDocument, change.Kind);
        Assert.Equal("stages/build.md", change.Subject);
    }

    [Fact]
    public void Read_QualityGateOnlyChange_IsDetectedFromTheRealFiles()
    {
        var source = ReadPackage(("quality/gates.md", "Gate: unit tests must pass."));
        var candidate = ReadPackage(("quality/gates.md", "Gate: unit tests are optional."));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.SemanticDocument, change.Kind);
        Assert.Equal("quality/gates.md", change.Subject);
    }

    [Fact]
    public void Read_EscalationOnlyChange_IsDetectedFromTheRealFiles()
    {
        var source = ReadPackage(("escalation/paths.md", "Escalate to the reviewer after two failures."));
        var candidate = ReadPackage(("escalation/paths.md", "Never escalate."));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.SemanticDocument, change.Kind);
        Assert.Equal("escalation/paths.md", change.Subject);
    }

    [Fact]
    public void Read_ScriptAndConfigOnlyChange_ProducesNoSemanticDifference()
    {
        var source = ReadPackage(
            ("scripts/run.ps1", "Write-Host 'release'"),
            ("config/app.json", """{"retries":3}"""));
        var candidate = ReadPackage(
            ("scripts/run.ps1", "Write-Host 'release'; Write-Host 'done'"),
            ("config/app.json", """{"retries":5}"""));

        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    [Fact]
    public void Read_ManifestModelBindingChangeOnly_ProducesNoSemanticDifference()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":"model-old"},"stages":["build"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"bindings":{"Executor":"model-new"},"stages":["build"],"declaredRoles":["Executor"]}"""));

        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    [Fact]
    public void Read_ManifestStageSectionChange_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["build","release"]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_ManifestQualityGateSectionChange_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"qualityGates":["tests"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"qualityGates":[]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_ManifestEscalationSectionChange_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"escalation":{"onFailure":true}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"escalation":{"onFailure":false}}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_ManifestRoleSetChange_IsReportedWithoutCopyingTheChangedNames()
    {
        var source = ReadPackage(("workflow.json", """{"declaredRoles":["Executor"]}"""));
        var candidate = ReadPackage(("workflow.json", """{"declaredRoles":["Executor","Reviewer"]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.DeclaredRoleSet, change.Kind);
        Assert.Equal("workflow.json#declaredRoles", change.Subject);
        Assert.Contains("added: 1 name(s), sha256:", change.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Reviewer", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ManifestEntrypointChange_IsReported()
    {
        var source = ReadPackage(("workflow.json", """{"entrypoints":["run.ps1"]}"""));
        var candidate = ReadPackage(("workflow.json", """{"entrypoints":["run.ps1","release.ps1"]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.EntrypointSet, change.Kind);
        Assert.Contains("added: 1 name(s), sha256:", change.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("release.ps1", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ManifestAddedOrRemoved_IsReported()
    {
        var source = ReadPackage(("README.md", "# Workflow"));
        var candidate = ReadPackage(
            ("README.md", "# Workflow"),
            ("manifest.json", """{"declaredRoles":["Executor"]}"""));

        var added = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestAvailability, added.Kind);
        Assert.Equal("manifest.json", added.Subject);

        var removed = Assert.Single(SemanticPackageComparison.Compare(candidate, source));

        Assert.Equal(SemanticChangeKind.ManifestAvailability, removed.Kind);
        Assert.Equal("manifest.json", removed.Subject);
    }
    [Fact]
    public void Read_UnparseableManifest_IsUnreadableAndFailsClosed()
    {
        var source = ReadPackage(("workflow.json", """{"declaredRoles":["Executor"]}"""));
        var candidate = ReadPackage(("workflow.json", "{ not json"));

        Assert.False(candidate.FindManifest("workflow.json")!.Readable);

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.UnverifiableContent, change.Kind);
        Assert.Contains("cannot be parsed confidently", change.Detail, StringComparison.Ordinal);
        Assert.True(change.NonClearable);
    }

    [Fact]
    public void Read_SemanticFileBeyondTheBoundedSize_IsUnverifiableAndFailsClosed()
    {
        var source = ReadPackage(
            ("README.md", "# Workflow"),
            ("stages/build.md", new string('a', SemanticPackageAnalyzer.MaxAnalyzedFileBytes + 1)));
        var candidate = ReadPackage(
            ("README.md", "# Workflow"),
            ("stages/build.md", "Stage 1: build."));

        Assert.Equal(new[] { "stages/build.md" }, source.UnverifiableFiles);

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.UnverifiableContent, change.Kind);
        Assert.Equal("stages/build.md", change.Subject);

        // An incomplete analysis is a gap in the evidence, not a difference the operator may accept.
        Assert.True(change.NonClearable);
    }

    [Fact]
    public void Read_UnreadableBinarySemanticFile_IsUnverifiableAndFailsClosed()
    {
        var directory = CreatePackageDirectory();
        var stages = Path.Combine(directory, "stages");

        Directory.CreateDirectory(stages);
        File.WriteAllBytes(Path.Combine(stages, "build.md"), new byte[] { 0x41, 0x00, 0x42 });
        File.WriteAllText(Path.Combine(directory, "README.md"), "# Workflow");

        var source = SemanticPackageAnalyzer.Read(directory);
        var candidate = ReadPackage(
            ("README.md", "# Workflow"),
            ("stages/build.md", "Stage 1: build."));

        Assert.Equal(new[] { "stages/build.md" }, source.UnverifiableFiles);

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.UnverifiableContent, change.Kind);
        Assert.Equal("stages/build.md", change.Subject);
        Assert.True(change.NonClearable);
    }

    [Fact]
    public void Read_PackageBeyondTheBoundedFileCount_IsReportedAsTruncated()
    {
        var entries = Enumerable.Range(0, SemanticPackageAnalyzer.MaxAnalyzedFiles + 5)
            .Select(index => ($"stages/step-{index:D4}.md", "step"))
            .ToArray();

        var facts = ReadPackage(entries);

        Assert.True(facts.AnalysisTruncated);
        Assert.Equal(SemanticPackageAnalyzer.MaxAnalyzedFiles, facts.DocumentDigests.Count);

        // Both sides exceed the budget here, so the guard reports one inconclusive comparison per side and
        // still compares the files it could read.
        var changes = SemanticPackageComparison.Compare(ReadPackage(entries), facts);

        Assert.Equal(2, changes.Count(detected => detected.Subject == "(package)"));
        Assert.All(
            changes.Where(detected => detected.Subject == "(package)"),
            detected =>
            {
                Assert.Equal(SemanticChangeKind.UnverifiableContent, detected.Kind);
                Assert.Contains("bounded semantic analysis limit", detected.Detail, StringComparison.Ordinal);
                Assert.True(detected.NonClearable);
            });
    }

    /// <summary>
    /// The file-count budget drops the tail of the sorted package, so a rewritten late-sorting stage file
    /// used to disappear from the comparison and leave only a generic package-level finding that an
    /// operator decision could clear. The dropped paths are now named, and the finding is never clearable.
    /// </summary>
    [Fact]
    public void Read_ChangedFileBeyondTheFileCountCutoff_IsNamedAndNeverClearable()
    {
        var source = ReadPackage(Enumerable.Range(0, SemanticPackageAnalyzer.MaxAnalyzedFiles + 5)
            .Select(index => ($"stages/step-{index:D4}.md", "step"))
            .ToArray());

        var changedTail = SemanticPackageAnalyzer.MaxAnalyzedFiles + 4;

        var candidate = ReadPackage(Enumerable.Range(0, SemanticPackageAnalyzer.MaxAnalyzedFiles + 5)
            .Select(index => index == changedTail
                ? ($"stages/step-{index:D4}.md", "Stage 1: build. Stage 2: publish the release.")
                : ($"stages/step-{index:D4}.md", "step"))
            .ToArray());

        var dropped = $"stages/step-{changedTail:D4}.md";

        Assert.Contains(dropped, source.UnverifiableFiles);
        Assert.Contains(dropped, candidate.UnverifiableFiles);

        var changes = SemanticPackageComparison.Compare(source, candidate);

        var named = Assert.Single(changes.Where(detected => detected.Subject == dropped));

        Assert.Equal(SemanticChangeKind.UnverifiableContent, named.Kind);
        Assert.Contains("bounded file budget", named.Detail, StringComparison.Ordinal);
        Assert.True(named.NonClearable);
        Assert.Contains(changes, detected => detected.Subject == "(package)" && detected.NonClearable);
    }

    [Fact]
    public void Compare_IdenticalPackages_ReportNothing()
    {
        var entries = new (string Path, string Content)[]
        {
            ("README.md", "# Workflow"),
            ("workflow.json", """{"declaredRoles":["Executor"],"bindings":{"Executor":"m"}}"""),
            ("prompts/executor.md", "You are the executor."),
            ("scripts/run.ps1", "Write-Host 'release'")
        };

        Assert.Empty(SemanticPackageComparison.Compare(ReadPackage(entries), ReadPackage(entries)));
    }

    // ---- Both root manifests are compared, and only exact routing leaves are ignored ------------------

    /// <summary>
    /// A candidate used to be able to keep or add a byte-identical <c>manifest.json</c>, put the rewritten
    /// stages, gates and escalation rules into the <c>workflow.json</c> the activation parser reads first,
    /// and produce an empty diff. Every root manifest is now compared on its own path.
    /// </summary>
    [Fact]
    public void Read_SplitManifests_RewrittenWorkflowJsonIsDetectedBesideAnUnchangedManifestJson()
    {
        var source = ReadPackage(
            ("manifest.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""),
            ("workflow.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var candidate = ReadPackage(
            ("manifest.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""),
            ("workflow.json", """{"declaredRoles":["Executor"],"stages":[]}"""));

        var changes = SemanticPackageComparison.Compare(source, candidate);

        var structure = Assert.Single(
            changes.Where(detected => detected.Kind == SemanticChangeKind.ManifestStructure));

        Assert.Equal("workflow.json", structure.Subject);

        // The two manifests now disagree, so which one a reader honours is a silent choice as well.
        Assert.Contains(
            changes,
            detected => detected.Kind == SemanticChangeKind.ManifestPrecedence
                && detected.Subject.Contains("workflow.json", StringComparison.Ordinal)
                && detected.Subject.Contains("manifest.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ManifestAddedBesideAnExistingManifest_ReportsBothTheAdditionAndThePrecedence()
    {
        var source = ReadPackage(("workflow.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var candidate = ReadPackage(
            ("manifest.json", """{"declaredRoles":["Executor"],"stages":["build","release"]}"""),
            ("workflow.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""));

        var changes = SemanticPackageComparison.Compare(source, candidate);

        var availability = Assert.Single(
            changes.Where(detected => detected.Kind == SemanticChangeKind.ManifestAvailability));

        Assert.Equal("manifest.json", availability.Subject);

        var precedence = Assert.Single(
            changes.Where(detected => detected.Kind == SemanticChangeKind.ManifestPrecedence));

        Assert.Contains("workflow.json", precedence.Subject, StringComparison.Ordinal);
        Assert.Contains("manifest.json", precedence.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_SameRootManifestNamesOnBothSides_ReportNoPrecedenceFinding()
    {
        var entries = new (string Path, string Content)[]
        {
            ("manifest.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""),
            ("workflow.json", """{"declaredRoles":["Executor"],"stages":["build"]}""")
        };

        Assert.Empty(SemanticPackageComparison.Compare(ReadPackage(entries), ReadPackage(entries)));
    }

    /// <summary>
    /// The whole <c>bindings</c> object used to be removed before the comparison, so a routine rebinding
    /// could carry a new escalation policy and still produce an empty diff. Only the routing leaves are
    /// ignored now, so a nested policy is detected even when the model mapping itself is untouched.
    /// </summary>
    [Fact]
    public void Read_NestedEscalationPolicyUnderBindings_IsDetectedWithAnUnchangedModelMapping()
    {
        var source = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a","escalation":{"onFailure":true}}}}
            """));
        var candidate = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a","escalation":{"onFailure":false}}}}
            """));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
        Assert.Equal("workflow.json", change.Subject);
    }

    [Fact]
    public void Read_ChangedModelMappingUnderBindings_StaysEqualToTheSource()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-b"}}}"""));

        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    [Fact]
    public void Read_NestedQualityGateUnderRoleBindings_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"roleBindings":[{"role":"Executor","targetModelId":"m","qualityGates":["tests"]}]}
            """));
        var candidate = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"roleBindings":[{"role":"Executor","targetModelId":"m","qualityGates":[]}]}
            """));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_NestedStagePolicyUnderMetadata_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"metadata":{"createdAtUtc":"2026-01-01T00:00:00Z","stages":[{"id":"build"}]}}
            """));
        var candidate = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"metadata":{"createdAtUtc":"2026-02-02T00:00:00Z","stages":[{"id":"build"},{"id":"ship"}]}}
            """));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    /// <summary>
    /// Only the top-level <c>bindings</c>/<c>roleBindings</c> map is routing, because that is all the
    /// execution parser reads as bindings. <c>creationMetadata</c> is provenance data, so a rewrite of it is
    /// a difference in the compared form instead of a silently allowed one — an ignore list applied by name
    /// would otherwise keep allowing the next provenance-shaped homonym.
    /// </summary>
    [Fact]
    public void Read_ProvenanceRewriteUnderCreationMetadata_IsReportedAsAStructureChange()
    {
        var source = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"creationMetadata":{"sourceVersionId":"v1","adapterRouteId":"acct-1"}}
            """));
        var candidate = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"creationMetadata":{"sourceVersionId":"v2","adapterRouteId":"acct-2"}}
            """));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_NestedRolePolicyUnderCreationMetadata_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"creationMetadata":{"rolePolicy":{"reviewer":"mandatory"}}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"creationMetadata":{"rolePolicy":{"reviewer":"optional"}}}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    /// <summary>
    /// A role-named scalar is the legacy <c>"Executor": "model-id"</c> binding shape only inside the
    /// binding containers. Elsewhere it is a claim about a role, so a rewrite of it is reported.
    /// </summary>
    [Fact]
    public void Read_RoleNamedScalarUnderMetadata_IsCompared()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"metadata":{"Escalation":"mandatory"}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"metadata":{"Escalation":"never"}}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Read_LegacyRoleToModelBindingShape_StaysEqualAcrossARebinding()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1:model-a"}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-2:model-b"}}"""));

        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    [Fact]
    public void Read_PolicyParkedUnderADeclaredNameKey_IsDetected()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"roles":[{"name":"Escalation","policy":"mandatory"}]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"roles":[{"name":"Escalation","policy":"never"}]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    [Fact]
    public void Compare_RejectsNullFacts()
    {
        Assert.Throws<ArgumentNullException>(() => SemanticPackageComparison.Compare(null!, SemanticPackageFacts.Empty));
        Assert.Throws<ArgumentNullException>(() => SemanticPackageComparison.Compare(SemanticPackageFacts.Empty, null!));
    }

    // ---- The routing ignore is positional, and a scalar container is never dropped -------------------

    /// <summary>
    /// The routing ignore used to travel with the container name down the whole subtree, so a scalar whose
    /// own name happens to be a routing leaf — <c>agent</c> here — stayed invisible at any depth. The model
    /// mapping is byte-identical in both packages, and the only rewrite is the stage's agent.
    /// </summary>
    [Theory]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}},"metadata":{"stages":[{"id":"build","agent":"executor"}]}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}},"metadata":{"stages":[{"id":"build","Agent":"executor"}]}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}},"metadata":{"bindings":{"stages":[{"agent":"executor"}]}}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}},"creationMetadata":{"roleBindings":{"stages":[{"Agent":"executor"}]}}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"roleBindings":[{"role":"Executor","targetModelId":"model-a"}],"metadata":{"roleBindings":{"stages":[{"agent":"executor"}]}}}""")]
    public void Read_NestedRoutingLeafNameBelowANonRoutingPosition_IsCompared(string sourceManifest)
    {
        var candidateManifest = sourceManifest.Replace("executor", "skip review", StringComparison.Ordinal);

        Assert.NotEqual(sourceManifest, candidateManifest);

        var source = ReadPackage(("workflow.json", sourceManifest));
        var candidate = ReadPackage(("workflow.json", candidateManifest));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
        Assert.Equal("workflow.json", change.Subject);
    }

    /// <summary>
    /// A routing leaf name is only a route while it sits on the routing surface or on a direct entry of it.
    /// The same <c>source</c> scalar one level deeper under a binding entry is a nested policy and is
    /// compared, while the entry's own <c>modelId</c> stays a route.
    /// </summary>
    [Fact]
    public void Read_NestedRoutingLeafNameBelowABindingEntry_IsCompared()
    {
        var source = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a","escalation":{"source":"review"}}}}
            """));
        var candidate = ReadPackage((
            "workflow.json",
            """
            {"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a","escalation":{"source":"skip review"}}}}
            """));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    /// <summary>
    /// A scalar container is a claim about routing or provenance written in prose, and it used to vanish
    /// before the diff: a candidate could add a <c>metadata</c> string and still compare equal to a source
    /// that has none.
    /// </summary>
    [Theory]
    [InlineData("metadata")]
    [InlineData("creationMetadata")]
    [InlineData("bindings")]
    [InlineData("roleBindings")]
    public void Read_ScalarContainerVersusAbsent_IsAStructuralDifference(string container)
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            "{\"declaredRoles\":[\"Executor\"],\"stages\":[\"build\"],\""
            + container
            + "\":\"escalation: never; quality gates: off\"}"));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
        Assert.Equal("workflow.json", change.Subject);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("7")]
    [InlineData("null")]
    public void Read_ScalarBindingContainerOnOneSideOnly_IsAStructuralDifference(string value)
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"model-a"}}}"""));
        var candidate = ReadPackage((
            "workflow.json",
            "{\"declaredRoles\":[\"Executor\"],\"bindings\":{\"Executor\":{\"modelId\":\"model-a\"}},"
            + "\"roleBindings\":" + value + "}"));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
    }

    /// <summary>
    /// A different scalar on each side is a rewrite like any other, and a rebinding-only map still compares
    /// equal to an absent one, which is what keeps a routine rebinding from fabricating a difference.
    /// </summary>
    [Fact]
    public void Read_RewrittenScalarContainer_IsCompared_AndARebindingOnlyMapStaysEqualToAnAbsentOne()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"metadata":"escalation: never"}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"metadata":"escalation: always"}"""));

        Assert.Equal(
            SemanticChangeKind.ManifestStructure,
            Assert.Single(SemanticPackageComparison.Compare(source, candidate)).Kind);

        var withMap = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1:model-a"}}"""));
        var withoutMap = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"]}"""));

        Assert.Empty(SemanticPackageComparison.Compare(withMap, withoutMap));
    }

    // ---- An object-shaped binding entry of routing leaves only is still a pure rebinding --------------

    /// <summary>
    /// Only a scalar entry was recognised as a route, so the richer <c>"Executor":{"modelId":"acct-1"}</c>
    /// shape kept an empty <c>{"Executor":{}}</c> in the compared form and fabricated a difference against
    /// an absent or legacy scalar binding. A rebinding is a rebinding whichever shape writes it, so the
    /// object entry is compared as the routing-only container it is.
    /// </summary>
    [Theory]
    [InlineData("""{"declaredRoles":["Executor"]}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1"}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"roleBindings":{"Executor":"acct-1"}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1"}}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-2:model-b"}}}""")]
    [InlineData("""{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1","fallbackModelId":"acct-2"}}}""")]
    public void Read_ObjectShapedBindingEntryOfRoutingLeavesOnly_ComparesEqualToAnAbsentOrScalarBinding(
        string candidateManifest)
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1"}}}"""));
        var candidate = ReadPackage(("workflow.json", candidateManifest));

        Assert.Equal(
            source.FindManifest("workflow.json")!.StructureForm,
            candidate.FindManifest("workflow.json")!.StructureForm);
        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    /// <summary>
    /// Two root manifests that spell the same routes differently still declare the same workflow, so a
    /// split-root package must not be reported as an ambiguous precedence decision over routing alone. The
    /// drop is decided per manifest, so the compared structure form is what proves it.
    /// </summary>
    [Theory]
    [InlineData(
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-1"}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1"}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-2"}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-2"}}""")]
    [InlineData(
        """{"declaredRoles":["Executor"],"bindings":{"Executor":"acct-1"}}""",
        """{"declaredRoles":["Executor"]}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-2"}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"acct-2"}}}""")]
    public void Read_SplitRootManifestsThatDifferOnlyInTheRoutingShape_ReportNoPrecedence(
        string sourceWorkflowJson,
        string sourceManifestJson,
        string candidateWorkflowJson,
        string candidateManifestJson)
    {
        var source = ReadPackage(
            ("manifest.json", sourceManifestJson),
            ("workflow.json", sourceWorkflowJson));
        var candidate = ReadPackage(
            ("manifest.json", candidateManifestJson),
            ("workflow.json", candidateWorkflowJson));

        Assert.Equal(
            source.FindManifest("workflow.json")!.StructureForm,
            source.FindManifest("manifest.json")!.StructureForm);
        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    /// <summary>
    /// The recursion stops at the entry, so one member that is not a route keeps the whole container in the
    /// comparison: a nested object, a nested array and even a routing-leaf name holding an object are
    /// policies, not routes.
    /// </summary>
    [Theory]
    [InlineData(
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"m","qualityGates":["tests"]}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"m","qualityGates":[]}}}""")]
    [InlineData(
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"m","review":{"required":true}}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":"m","review":{"required":false}}}}""")]
    [InlineData(
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":{"policy":"mandatory"}}}}""",
        """{"declaredRoles":["Executor"],"bindings":{"Executor":{"modelId":{"policy":"optional"}}}}""")]
    [InlineData(
        """{"declaredRoles":["Executor"],"roleBindings":[{"targetModelId":"m","escalation":{"onFailure":true}}]}""",
        """{"declaredRoles":["Executor"],"roleBindings":[{"targetModelId":"m","escalation":{"onFailure":false}}]}""")]
    public void Read_BindingEntryThatCarriesAnythingButRoutes_IsCompared(
        string sourceManifest,
        string candidateManifest)
    {
        var change = Assert.Single(
            SemanticPackageComparison.Compare(
                ReadPackage(("workflow.json", sourceManifest)),
                ReadPackage(("workflow.json", candidateManifest))));

        Assert.Equal(SemanticChangeKind.ManifestStructure, change.Kind);
        Assert.Equal("workflow.json", change.Subject);
    }

    // ---- A declared-name list past the bound is unverifiable, never an empty diff ---------------------

    /// <summary>
    /// Only the ordinally first 64 declared names survive the bound, while the manifest parser reads every
    /// entry, so a rewrite past the cap used to produce an empty comparison. The census counts the raw array
    /// entries before de-duplication, so a duplicated or case-variant entry still counts, and 65 arbitrary
    /// role strings count even though the parser would reject them as role names.
    /// </summary>
    [Theory]
    [InlineData("declaredRoles", "role-")]
    [InlineData("entrypoints", "entry-")]
    public void Read_DeclaredNamesBeyondTheBoundedCap_AreUnverifiableAndNeverClearable(
        string section,
        string prefix)
    {
        var cap = SemanticPackageAnalyzer.MaxDeclaredNames;
        var source = ReadDeclaredNamePackage(section, prefix, cap + 1);
        var candidate = ReadDeclaredNamePackage(section, prefix, cap + 1, changedTail: true);

        var manifest = source.FindManifest("workflow.json")!;

        Assert.True(
            section == "declaredRoles" ? manifest.DeclaredRolesTruncated : manifest.EntrypointsTruncated);
        Assert.Equal(cap, manifest.DeclaredRoles.Count + manifest.Entrypoints.Count);

        var changes = SemanticPackageComparison.Compare(source, candidate);

        var unverifiable = Assert.Single(
            changes.Where(detected => detected.Kind == SemanticChangeKind.UnverifiableContent));

        Assert.Equal("workflow.json", unverifiable.Subject);
        Assert.Contains(section, unverifiable.Detail, StringComparison.Ordinal);
        Assert.Contains($"limit of {cap} entries", unverifiable.Detail, StringComparison.Ordinal);
        Assert.True(unverifiable.NonClearable);
    }

    /// <summary>
    /// A duplicate or a case variant past the cap is an entry the parser still reads, so it has to be counted
    /// towards the census even though de-duplication would collapse it.
    /// </summary>
    [Fact]
    public void Read_DuplicateAndCaseVariantEntriesBeyondTheBoundedCap_AreUnverifiable()
    {
        var cap = SemanticPackageAnalyzer.MaxDeclaredNames;
        var names = Enumerable.Range(0, cap).Select(index => $"role-{index:D4}").ToArray();

        // One exact duplicate and one case variant of a name that is already inside the kept prefix, both
        // beyond the cap, so the de-duplicated prefix is byte-identical on both sides.
        var source = ReadPackage((
            "workflow.json",
            $$"""{"declaredRoles":[{{string.Join(",", names.Select(name => $"\"{name}\""))}},"role-0000","ROLE-0001"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            $$"""{"declaredRoles":[{{string.Join(",", names.Select(name => $"\"{name}\""))}},"role-0000","ROLE-0001"]}"""));

        Assert.True(source.FindManifest("workflow.json")!.DeclaredRolesTruncated);
        Assert.Equal(cap, source.FindManifest("workflow.json")!.DeclaredRoles.Count);

        // The only difference is past the cap, so prefix equality says "equal" and the census is the gate.
        var changes = SemanticPackageComparison.Compare(source, candidate);

        Assert.Contains(
            changes,
            detected => detected.Kind == SemanticChangeKind.UnverifiableContent
                && detected.Subject == "workflow.json"
                && detected.NonClearable);
    }

    /// <summary>
    /// The concrete AC5 case: 65 unique entrypoint names, the last one rewritten. The kept prefix stops at
    /// the cap, so the two kept lists and the two structure forms are byte-identical and only the census can
    /// see the rewrite — which is exactly why it has to be a never-clearable finding and not an empty diff.
    /// </summary>
    [Fact]
    public void Read_RewrittenUniqueNamePastTheBoundedCap_IsReportedByTheCensusAloneAndNeverClears()
    {
        var cap = SemanticPackageAnalyzer.MaxDeclaredNames;
        var rewrittenTail = $"entry-{cap:D4}-rewritten";

        var source = ReadDeclaredNamePackage("entrypoints", "entry-", cap + 1);
        var candidate = ReadDeclaredNamePackage("entrypoints", "entry-", cap + 1, changedTail: true);

        var sourceManifest = source.FindManifest("workflow.json")!;
        var candidateManifest = candidate.FindManifest("workflow.json")!;

        Assert.Equal(cap, sourceManifest.Entrypoints.Count);
        Assert.DoesNotContain(rewrittenTail, sourceManifest.Entrypoints);
        Assert.DoesNotContain(rewrittenTail, candidateManifest.Entrypoints);
        Assert.Equal(sourceManifest.Entrypoints, candidateManifest.Entrypoints);
        Assert.Equal(sourceManifest.StructureForm, candidateManifest.StructureForm);
        Assert.True(sourceManifest.EntrypointsTruncated);
        Assert.True(candidateManifest.EntrypointsTruncated);

        var unverifiable = Assert.Single(
            SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.UnverifiableContent, unverifiable.Kind);
        Assert.Equal("workflow.json", unverifiable.Subject);
        Assert.Contains("entrypoints", unverifiable.Detail, StringComparison.Ordinal);
        Assert.True(unverifiable.NonClearable);
    }

    [Fact]
    public void Read_DeclaredNamesExactlyAtTheBoundedCap_AreFullyVerifiable()
    {
        var source = ReadDeclaredNamePackage("entrypoints", "entry-", SemanticPackageAnalyzer.MaxDeclaredNames);
        var candidate = ReadDeclaredNamePackage("entrypoints", "entry-", SemanticPackageAnalyzer.MaxDeclaredNames);

        Assert.False(source.FindManifest("workflow.json")!.EntrypointsTruncated);
        Assert.Empty(SemanticPackageComparison.Compare(source, candidate));
    }

    // ---- Every clearable manifest finding names the digests it was decided on -----------------------

    [Fact]
    public void Read_RewrittenManifestFinding_NamesBothStructureDigests()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var candidate = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["ship"]}"""));

        var change = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        var sourceDigest = StructureDigest(source, "workflow.json");
        var candidateDigest = StructureDigest(candidate, "workflow.json");

        Assert.Equal(64, sourceDigest.Length);
        Assert.Equal(64, candidateDigest.Length);
        Assert.NotEqual(sourceDigest, candidateDigest);
        Assert.Contains($"source sha256:{sourceDigest}", change.Detail, StringComparison.Ordinal);
        Assert.Contains($"candidate sha256:{candidateDigest}", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AddedAndRemovedManifestFindings_NameThePresentSideAgainstAnAbsentMarker()
    {
        var source = ReadPackage(("README.md", "# Workflow"));
        var candidate = ReadPackage(
            ("README.md", "# Workflow"),
            ("manifest.json", """{"declaredRoles":["Executor"]}"""));

        var added = Assert.Single(SemanticPackageComparison.Compare(source, candidate));

        Assert.Equal(SemanticChangeKind.ManifestAvailability, added.Kind);
        Assert.Contains("source sha256:absent", added.Detail, StringComparison.Ordinal);
        Assert.Contains(
            $"candidate sha256:{StructureDigest(candidate, "manifest.json")}",
            added.Detail,
            StringComparison.Ordinal);

        var removed = Assert.Single(SemanticPackageComparison.Compare(candidate, source));

        Assert.Equal(SemanticChangeKind.ManifestAvailability, removed.Kind);
        Assert.Contains(
            $"source sha256:{StructureDigest(candidate, "manifest.json")}",
            removed.Detail,
            StringComparison.Ordinal);
        Assert.Contains("candidate sha256:absent", removed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DisagreeingManifestSetFinding_NamesEveryRootManifestOfTheSet()
    {
        var shared = """{"declaredRoles":["Executor"],"stages":["build"]}""";

        var source = ReadPackage(
            ("manifest.json", shared),
            ("workflow.json", shared));
        var candidate = ReadPackage(
            ("manifest.json", shared),
            ("workflow.json", """{"declaredRoles":["Executor"],"stages":[]}"""));

        var precedence = Assert.Single(
            SemanticPackageComparison.Compare(source, candidate)
                .Where(detected => detected.Kind == SemanticChangeKind.ManifestPrecedence));

        foreach (var path in new[] { "manifest.json", "workflow.json" })
        {
            Assert.Contains(
                $"{path} source sha256:{StructureDigest(source, path)}, "
                + $"candidate sha256:{StructureDigest(candidate, path)}",
                precedence.Detail,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The identity the activation gate matches is the reported message, so two decisions for the same
    /// manifest and the same kind must be distinguishable: a decision for <c>["build"]</c> can never
    /// authorise <c>["ship"]</c>.
    /// </summary>
    [Fact]
    public void Read_TwoRewritesOfTheSameManifest_ProduceDifferentIdentities()
    {
        var source = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["build"]}"""));
        var first = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["review"]}"""));
        var second = ReadPackage((
            "workflow.json",
            """{"declaredRoles":["Executor"],"stages":["ship"]}"""));

        var forFirst = Identity(SemanticPackageComparison.Compare(source, first));
        var forSecond = Identity(SemanticPackageComparison.Compare(source, second));

        Assert.NotEqual(forFirst, forSecond);
    }

    private static string Identity(IReadOnlyList<SemanticChange> changes) => string.Join(
        " | ",
        changes.Select(change => change.ToString()));

    private static string StructureDigest(SemanticPackageFacts facts, string path) => Convert
        .ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(facts.FindManifest(path)!.StructureForm!)))
        .ToLowerInvariant();

    private SemanticPackageFacts ReadDeclaredNamePackage(
        string section,
        string prefix,
        int count,
        bool changedTail = false)
    {
        var names = Enumerable
            .Range(0, count)
            .Select(index => changedTail && index == count - 1
                ? $"\"{prefix}{index:D4}-rewritten\""
                : $"\"{prefix}{index:D4}\"")
            .ToArray();

        return ReadPackage((
            "workflow.json",
            $$"""{"{{section}}":[{{string.Join(",", names)}}]}"""));
    }

    private SemanticPackageFacts ReadPackage(params (string Path, string Content)[] entries)
    {
        var directory = CreatePackageDirectory();

        foreach (var (path, content) in entries)
        {
            var fullPath = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        return SemanticPackageAnalyzer.Read(directory);
    }

    private string CreatePackageDirectory()
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        return directory;
    }
}

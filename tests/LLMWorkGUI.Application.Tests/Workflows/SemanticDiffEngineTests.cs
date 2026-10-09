using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class SemanticDiffEngineTests
{
    private readonly SemanticDiffEngine _engine = new();

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[\"Executor\",\"unrecognized-role\"]")]
    public void Compare_UnreadableDeclaredRoleBaseline_CannotBeClearedByExpandedScope(string baseline)
    {
        var result = _engine.Compare(baseline,
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") }, allowExpandedSemanticScope: true);

        Assert.True(result.HasBlockers);
        Assert.Contains(result.Issues, issue => issue.IsNotClearable);
    }

    [Fact]
    public void Compare_NullDeclaredRoles_AllowsRebindingExistingRoles()
    {
        var result = _engine.Compare(
            declaredRolesJson: null,
            new[]
            {
                CreateMapping(WorkflowRole.Executor, "model-1"),
                CreateMapping(WorkflowRole.Reviewer, "model-2")
            },
            allowExpandedSemanticScope: false);

        Assert.Empty(result.Issues);
        Assert.False(result.HasSemanticChanges);
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void Compare_EmptyDeclaredRolesArray_AllowsRebindingExistingRoles()
    {
        var result = _engine.Compare(
            "[]",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Compare_DeclaredRolesMatchMappings_IsNotBlocked()
    {
        var result = _engine.Compare(
            """["Executor","Reviewer"]""",
            new[]
            {
                CreateMapping(WorkflowRole.Reviewer, "model-2"),
                CreateMapping(WorkflowRole.Executor, "model-1")
            },
            allowExpandedSemanticScope: false);

        Assert.Empty(result.Issues);
        Assert.False(result.HasSemanticChanges);
    }

    [Fact]
    public void Compare_DeclaredRolesParsedCaseInsensitively()
    {
        var result = _engine.Compare(
            """["executor","REVIEWER"]""",
            new[]
            {
                CreateMapping(WorkflowRole.Executor, "model-1"),
                CreateMapping(WorkflowRole.Reviewer, "model-2")
            },
            allowExpandedSemanticScope: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Compare_AddedRole_WhenScopeNotExpanded_FlagsDisallowedSemanticChange()
    {
        var result = _engine.Compare(
            """["Executor"]""",
            new[]
            {
                CreateMapping(WorkflowRole.Executor, "model-1"),
                CreateMapping(WorkflowRole.Coordinator, "model-2")
            },
            allowExpandedSemanticScope: false);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal("Coordinator", issue.Role);
        Assert.Contains("added", issue.Message, StringComparison.Ordinal);
        Assert.True(result.HasSemanticChanges);
        Assert.True(result.HasBlockers);
    }

    [Fact]
    public void Compare_RemovedRole_WhenScopeNotExpanded_FlagsDisallowedSemanticChange()
    {
        var result = _engine.Compare(
            """["Executor","Escalation"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal("Escalation", issue.Role);
        Assert.Contains("no longer mapped", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_RenamedRole_FlagsAddedAndRemovedRoles()
    {
        var result = _engine.Compare(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Reviewer, "model-1") },
            allowExpandedSemanticScope: false);

        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.Equal(
            AdaptationBlockerKind.DisallowedSemanticChange,
            issue.Kind));
    }

    [Fact]
    public void Compare_SemanticChangeMapping_WhenScopeNotExpanded_FlagsDisallowedSemanticChange()
    {
        var result = _engine.Compare(
            declaredRolesJson: null,
            new[]
            {
                new SemanticRoleMapping(
                    WorkflowRole.Escalation,
                    "route-old",
                    "route-new",
                    "model-1",
                    "Escalation semantics would change.",
                    isSemanticChange: true)
            },
            allowExpandedSemanticScope: false);

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal("Escalation", issue.Role);
    }

    [Fact]
    public void Compare_ExpandedScope_StillReportsTheRoleDriftAsABlocker()
    {
        var result = _engine.Compare(
            """["Executor"]""",
            new[]
            {
                new SemanticRoleMapping(
                    WorkflowRole.Coordinator,
                    "route-old",
                    "route-new",
                    "model-1",
                    "Adds a coordinator.",
                    isSemanticChange: true)
            },
            allowExpandedSemanticScope: true);

        var issue = Assert.Single(
            result.Issues,
            candidate => candidate.Role == "Coordinator"
                && candidate.Message.Contains("added", StringComparison.Ordinal));

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.True(result.HasSemanticChanges);
        Assert.True(result.HasBlockers);
    }

    [Fact]
    public void Compare_SupportsCoordinatorRole()
    {
        var result = _engine.Compare(
            """["Coordinator"]""",
            new[] { CreateMapping(WorkflowRole.Coordinator, "model-1") },
            allowExpandedSemanticScope: false);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Compare_InvalidDeclaredRolesJson_IsAnUnclearableBlocker()
    {
        var result = _engine.Compare(
            "{ not json",
            new[]
            {
                CreateMapping(WorkflowRole.Executor, "model-1"),
                CreateMapping(WorkflowRole.Reviewer, "model-2")
            },
            allowExpandedSemanticScope: false);

        Assert.True(Assert.Single(result.Issues).IsNotClearable);
    }

    [Fact]
    public void Compare_RejectsNullMappings()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.Compare(
            declaredRolesJson: null,
            null!,
            allowExpandedSemanticScope: false));
    }

    [Fact]
    public void Compare_RejectsNullRequest()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.Compare((SemanticDiffRequest)null!));
    }

    [Fact]
    public void Compare_RejectsNullMappingsInRequest()
    {
        Assert.Throws<ArgumentNullException>(() => _engine.Compare(new SemanticDiffRequest(
            declaredRolesJson: null,
            mappings: null!,
            allowExpandedSemanticScope: false)));
    }

    /// <summary>
    /// An empty mapping list is a removal of every declared role, not "no opinion". The previous activation
    /// path short-circuited on <c>mappings.Count == 0</c> and let a candidate drop all roles silently.
    /// </summary>
    [Fact]
    public void Compare_EmptyMappingsAgainstNonEmptyDeclaredRoles_BlocksEveryDeclaredRole()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            Array.Empty<SemanticRoleMapping>(),
            allowExpandedSemanticScope: false));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal("Executor", issue.Role);
        Assert.Contains("no longer mapped", issue.Message, StringComparison.Ordinal);
        Assert.True(result.HasBlockers);
    }

    [Fact]
    public void Compare_EmptyMappingsWithEmptyDeclaredRoles_ReportsNothing()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            "[]",
            Array.Empty<SemanticRoleMapping>(),
            allowExpandedSemanticScope: false));

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Compare_RewrittenSemanticDocument_BlocksEvenWithoutASemanticRoleChange()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(documents: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompts/executor.md"] = "source-digest"
            }),
            CreateFacts(documents: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompts/executor.md"] = "candidate-digest"
            })));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Null(issue.Role);
        Assert.Contains("prompts/executor.md", issue.Message, StringComparison.Ordinal);
        Assert.Contains("source-digest", issue.Message, StringComparison.Ordinal);
        Assert.Contains("candidate-digest", issue.Message, StringComparison.Ordinal);

        var change = Assert.Single(result.DetectedChanges);

        Assert.Equal(SemanticChangeKind.SemanticDocument, change.Kind);
        Assert.Equal("prompts/executor.md", change.Subject);
    }

    [Fact]
    public void Compare_RewrittenManifestSection_BlocksEvenWithoutASemanticRoleChange()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(roles: ["Executor"], structureForm: """{"stages":["build"]}"""),
            CreateFacts(roles: ["Executor"], structureForm: """{"stages":["build","release"]}""")));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("workflow.json", issue.Message, StringComparison.Ordinal);
        Assert.Equal(SemanticChangeKind.ManifestStructure, Assert.Single(result.DetectedChanges).Kind);
    }

    /// <summary>
    /// A pure rebinding of <c>bindings</c> is what adaptation is allowed to do, and the structural form
    /// excludes that section, so the guard must stay silent for it.
    /// </summary>
    [Fact]
    public void Compare_ChangedManifestModelBindingsOnly_IsNotASemanticChange()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(
                roles: ["Executor"],
                structureForm: """{"stages":["build"]}"""),
            CreateFacts(
                roles: ["Executor"],
                structureForm: """{"stages":["build"]}""")));

        Assert.Empty(result.Issues);
        Assert.Empty(result.DetectedChanges);
    }

    [Fact]
    public void Compare_ChangedManifestRoleSet_Blocks()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(roles: ["Executor"]),
            CreateFacts(roles: ["Executor", "Reviewer"])));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.DoesNotContain("Reviewer", issue.Message, StringComparison.Ordinal);
        Assert.Contains("added: 1 name(s), sha256:", issue.Message, StringComparison.Ordinal);
        Assert.Equal(SemanticChangeKind.DeclaredRoleSet, Assert.Single(result.DetectedChanges).Kind);
    }

    [Fact]
    public void Compare_ChangedManifestEntrypoints_Blocks()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            declaredRolesJson: null,
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(entrypoints: ["run.ps1"]),
            CreateFacts(entrypoints: ["run.ps1", "release.ps1"])));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Equal(SemanticChangeKind.EntrypointSet, Assert.Single(result.DetectedChanges).Kind);
    }

    [Fact]
    public void Compare_UnreadableSemanticDocument_FailsClosed()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            declaredRolesJson: null,
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            new SemanticPackageFacts
            {
                DocumentDigests = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["quality/gates.md"] = "source-digest"
                }
            },
            new SemanticPackageFacts
            {
                DocumentDigests = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["quality/gates.md"] = "source-digest"
                },
                UnverifiableFiles = ["quality/gates.md"]
            }));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("cannot be compared confidently", issue.Message, StringComparison.Ordinal);
        Assert.Equal(SemanticChangeKind.UnverifiableContent, Assert.Single(result.DetectedChanges).Kind);
    }

    [Fact]
    public void Compare_UnparseableManifest_FailsClosed()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            declaredRolesJson: null,
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            CreateFacts(manifestPath: "workflow.json", readable: true),
            CreateFacts(manifestPath: "workflow.json", readable: false)));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("cannot be parsed confidently", issue.Message, StringComparison.Ordinal);
        Assert.True(issue.IsNotClearable);
    }

    /// <summary>
    /// A semantic change is only ever cleared by the post-diff, per-issue acknowledgement of the activation
    /// gate, which runs against the freshly revalidated issue list. The pre-send scope flag is prompt
    /// context and nothing more, so it must not suppress a single detected difference.
    /// </summary>
    [Fact]
    public void Compare_PreSendScopeFlag_DoesNotClearAPackageChange()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: true,
            CreateFacts(documents: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stages/build.md"] = "source-digest"
            }),
            CreateFacts(documents: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stages/build.md"] = "candidate-digest"
            })));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.Contains("stages/build.md", issue.Message, StringComparison.Ordinal);
        Assert.False(issue.IsNotClearable);
        Assert.Single(result.DetectedChanges);
    }

    /// <summary>
    /// A package whose bounded file budget was exhausted is an incomplete analysis, not a difference, so
    /// the reported issue can never be acknowledged away at activation.
    /// </summary>
    [Fact]
    public void Compare_TruncatedPackageAnalysis_ReportsAnUnclearableBlocker()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            declaredRolesJson: null,
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            new SemanticPackageFacts { AnalysisTruncated = true },
            new SemanticPackageFacts()));

        var issue = Assert.Single(result.Issues);

        Assert.Equal(AdaptationBlockerKind.DisallowedSemanticChange, issue.Kind);
        Assert.True(issue.IsNotClearable);
        Assert.True(Assert.Single(result.DetectedChanges).NonClearable);
    }

    [Fact]
    public void Compare_SplitManifestRewrittenWorkflowJson_Blocks()
    {
        var source = new SemanticPackageFacts
        {
            Manifests =
            [
                CreateManifest("manifest.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""),
                CreateManifest("workflow.json", """{"declaredRoles":["Executor"],"stages":["build"]}""")
            ]
        };

        var candidate = new SemanticPackageFacts
        {
            Manifests =
            [
                CreateManifest("manifest.json", """{"declaredRoles":["Executor"],"stages":["build"]}"""),
                CreateManifest("workflow.json", """{"declaredRoles":["Executor"],"stages":[]}""")
            ]
        };

        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false,
            source,
            candidate));

        Assert.Contains(
            result.Issues,
            issue => issue.Kind == AdaptationBlockerKind.DisallowedSemanticChange
                && issue.Message.Contains("workflow.json", StringComparison.Ordinal));
        Assert.Contains(
            result.DetectedChanges,
            change => change.Kind == SemanticChangeKind.ManifestStructure
                && change.Subject == "workflow.json");
    }

    [Fact]
    public void Compare_WithoutPackageFacts_ReportsOnlyRoleSemantics()
    {
        var result = _engine.Compare(new SemanticDiffRequest(
            """["Executor"]""",
            new[] { CreateMapping(WorkflowRole.Executor, "model-1") },
            allowExpandedSemanticScope: false));

        Assert.Empty(result.Issues);
        Assert.Empty(result.DetectedChanges);
    }

    private static SemanticManifestFacts CreateManifest(string path, string json) => new(
        path,
        readable: true,
        structureForm: json);

    private static SemanticPackageFacts CreateFacts(
        string manifestPath = "workflow.json",
        bool readable = true,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? entrypoints = null,
        IReadOnlyDictionary<string, string>? documents = null,
        string structureForm = "{}") => new()
    {
        Manifests =
        [
            new SemanticManifestFacts(
                manifestPath,
                readable,
                structureForm,
                roles ?? Array.Empty<string>(),
                entrypoints ?? Array.Empty<string>())
        ],
        DocumentDigests = documents
            ?? new Dictionary<string, string>(StringComparer.Ordinal)
    };

    private static SemanticRoleMapping CreateMapping(WorkflowRole role, string targetModelId)
    {
        return new SemanticRoleMapping(
            role,
            "account-1:model-old",
            "account-1:" + targetModelId,
            targetModelId,
            "Rebinding an existing role.",
            isSemanticChange: false);
    }
}

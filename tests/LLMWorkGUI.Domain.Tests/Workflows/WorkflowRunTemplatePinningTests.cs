using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

/// <summary>
/// A run carries two independent identities. These tests cover only the second one - the assigned
/// template identity and its two snapshots - and the rule that the four values are written once and are
/// never replaced afterwards.
/// </summary>
public sealed class WorkflowRunTemplatePinningTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-2";
    private const string GraphSnapshot = """{"entryNodeId":"node-a","nodes":[]}""";
    private const string SchemeSnapshot = """{"initialStageId":"node-a","stages":[]}""";

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartPinnedToTemplate_RecordsBothIdentitiesAndBothSnapshots()
    {
        var run = CreatePinnedRun();

        Assert.Equal(VersionId, run.WorkflowVersionId);
        Assert.Equal("linear-template", run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal(GraphSnapshot, run.TemplateGraphSnapshotJson);
        Assert.Equal(SchemeSnapshot, run.TemplateSchemeSnapshotJson);
        Assert.True(run.IsTemplateBacked);
    }

    [Fact]
    public void Start_RecordsTheSourceVersionAndNoTemplateAtAll()
    {
        var run = WorkflowRun.Start(
            "run-legacy",
            ProjectId,
            PackageId,
            VersionId,
            sessionId: null,
            CreateStage("node-a"),
            BaseTime);

        Assert.Equal(VersionId, run.WorkflowVersionId);
        Assert.Null(run.TemplateId);
        Assert.Null(run.TemplateVersion);
        Assert.Null(run.TemplateGraphSnapshotJson);
        Assert.Null(run.TemplateSchemeSnapshotJson);
        Assert.False(run.IsTemplateBacked);
    }

    [Fact]
    public void ThePinnedIdentityAndItsSnapshotsAreNeverReplaced()
    {
        var properties = new[]
        {
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.TemplateId)),
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.TemplateVersion)),
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.TemplateGraphSnapshotJson)),
            typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.TemplateSchemeSnapshotJson))
        };

        foreach (var property in properties)
        {
            Assert.NotNull(property);
            Assert.Null(property!.SetMethod);
        }
    }

    [Theory]
    [InlineData("linear-template", null, GraphSnapshot, SchemeSnapshot)]
    [InlineData(null, 1, GraphSnapshot, SchemeSnapshot)]
    [InlineData("linear-template", 1, null, SchemeSnapshot)]
    [InlineData("linear-template", 1, GraphSnapshot, null)]
    public void AHalfPinnedRunIsRefused(
        string? templateId,
        int? templateVersion,
        string? graphSnapshot,
        string? schemeSnapshot)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            "run-half",
            ProjectId,
            PackageId,
            VersionId,
            sessionId: null,
            WorkflowRunState.Running,
            "node-a",
            "Role",
            BaseTime,
            endedAtUtc: null,
            WorkflowTerminalOutcome.None,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>(),
            templateId,
            templateVersion,
            graphSnapshot,
            schemeSnapshot));
    }

    [Fact]
    public void APinnedRunMustNameAVersionAboveZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowRun(
            "run-zero",
            ProjectId,
            PackageId,
            VersionId,
            sessionId: null,
            WorkflowRunState.Running,
            "node-a",
            "Role",
            BaseTime,
            endedAtUtc: null,
            WorkflowTerminalOutcome.None,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>(),
            "linear-template",
            0,
            GraphSnapshot,
            SchemeSnapshot));
    }

    private static WorkflowRun CreatePinnedRun() =>
        WorkflowRun.StartPinnedToTemplate(
            "run-pinned",
            ProjectId,
            PackageId,
            VersionId,
            sessionId: null,
            CreateStage("node-a"),
            BaseTime,
            "linear-template",
            1,
            GraphSnapshot,
            SchemeSnapshot);

    private static WorkflowStageDefinition CreateStage(string stageId) =>
        new(
            stageId,
            "Node A",
            "Role",
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId: null,
            failureStageId: null);
}

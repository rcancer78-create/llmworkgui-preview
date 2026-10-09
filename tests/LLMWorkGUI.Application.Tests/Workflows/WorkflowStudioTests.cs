using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// TASK-058 Phase 10E: Workflow Studio, versioned templates, document drafts, separate reviewer
/// verdicts, the pre-coder approval gate and the AGY/Codex account-context switch. The switch has no
/// session id factory and no caller-supplied observed route: on this host it refuses with the named
/// missing native-switch proof. Deterministic models are represented by fixed clocks, a fixed draft id
/// factory and hand-written fakes only.
/// </summary>
public sealed partial class WorkflowStudioTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveTemplate_RefusesUserTemplateThatShadowsBuiltInIdentity()
    {
        var context = new StudioContext();
        var builtIn = context.Studio.GetBuiltInTemplates()[0];
        var shadow = builtIn.Clone(builtIn.TemplateId, "Shadow");

        await Assert.ThrowsAsync<WorkflowValidationException>(() => context.Studio.SaveTemplateAsync(shadow));
        Assert.Same(builtIn, await context.Studio.GetRequiredTemplateAsync(builtIn.TemplateId, builtIn.Version));
    }

    [Fact]
    public void BuiltInTemplate_IsValidAndRequiresAllSevenDocuments()
    {
        var context = new StudioContext();

        var builtIn = Assert.Single(context.Studio.GetBuiltInTemplates());

        Assert.True(builtIn.IsBuiltIn);
        Assert.Equal(1, builtIn.Version);
        Assert.Equal(WorkflowStudioService.StandardTemplateId, builtIn.TemplateId);
        Assert.True(context.Studio.ValidateTemplateGraph(builtIn.Graph).IsValid);

        Assert.Equal(
            WorkflowStudioDocumentRules.RequiredDocumentKinds.OrderBy(kind => kind),
            builtIn.RequiredDocumentTemplates.OrderBy(kind => kind));
        Assert.Equal(7, builtIn.RequiredDocumentTemplates.Count);

        Assert.Equal(
            WorkflowStudioDocumentRules.RequiredReviewerRoles,
            context.Studio.DocumentReviewerRoles);
    }

    [Fact]
    public async Task CloneAndVersionTemplates_NeverMutateTheSourceTemplateAndRejectImplicitOverwrites()
    {
        var context = new StudioContext();
        var source = context.Studio.GetBuiltInTemplates()[0];
        var sourceNodeIds = source.Graph.Nodes.Select(node => node.NodeId).ToArray();
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var activeRun = WorkflowRun.Start(
            "run-active",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            scheme.GetRequiredStage(scheme.InitialStageId),
            Now);

        context.Runs.ActiveRun = activeRun;
        context.Packages.Packages = new[] { CreatePackage("pkg-1", Hash('a')) };

        var clone = await context.Studio.CloneTemplateAsync(
            source.TemplateId,
            "custom-workflow",
            "Custom workflow");

        Assert.False(clone.IsBuiltIn);
        Assert.Equal(1, clone.Version);
        Assert.Equal("custom-workflow", clone.TemplateId);
        Assert.Equal(sourceNodeIds, clone.Graph.Nodes.Select(node => node.NodeId));


        var versionTwo = await context.Studio.CreateTemplateVersionAsync(
            clone.TemplateId,
            2,
            sourceVersion: 1);

        var templates = await context.Studio.ListTemplatesAsync();

        Assert.Contains(
            templates,
            template => template.TemplateId == "custom-workflow" && template.Version == 1);
        Assert.Contains(
            templates,
            template => template.TemplateId == "custom-workflow" && template.Version == 2);

        // The shipped source template is a different, untouched instance.
        Assert.True(source.IsBuiltIn);
        Assert.Equal(sourceNodeIds, source.Graph.Nodes.Select(node => node.NodeId));
        Assert.Equal(1, source.Version);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => context.Studio.SaveTemplateAsync(clone));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => context.Studio.SaveTemplateAsync(source));

        // The template work above never touched the active run or the imported ZIP: the fakes throw on a
        // write, and the stored instances are still the same objects with the same original hash.
        Assert.Same(activeRun, context.Runs.ActiveRun);
        var package = Assert.Single(context.Packages.Packages);
        Assert.Equal(Hash('a'), package.OriginalHash);
    }

    [Fact]
    public async Task CloningAndVersioningATemplateKeepEveryNodesStageGates()
    {
        var context = new StudioContext();
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var source = context.Studio.GetBuiltInTemplates()[0];

        var clone = await context.Studio.CloneTemplateAsync(
            source.TemplateId,
            "gated-workflow",
            "Gated workflow");


        var versionTwo = await context.Studio.CreateTemplateVersionAsync("gated-workflow", 2);

        foreach (var template in new[] { clone, versionTwo, source })
        {
            foreach (var stage in scheme.Stages)
            {
                var gate = template.Graph.GetRequiredNode(stage.StageId).GateMetadata;

                Assert.NotNull(gate);
                Assert.Equal(stage.StageKind, gate!.StageKind);
                Assert.Equal(stage.RequiredReviewerRoles, gate.RequiredReviewerRoles);
                Assert.Equal(stage.RequiresUserApproval, gate.RequiresUserApproval);
                Assert.Equal(stage.ArtifactRequirement, gate.ArtifactRequirement);
            }
        }

        // And the gates survive the snapshot a run pins from whichever version it was assigned.
        foreach (var template in new[] { clone, versionTwo })
        {
            var pinned = WorkflowGraphSnapshot.Serialize(template.Graph);
            var rebuilt = WorkflowGraphSnapshot.Deserialize(pinned, "run-1");

            Assert.Equal(pinned, WorkflowGraphSnapshot.Serialize(rebuilt));
            rebuilt.Validate();
        }
    }

    [Fact]
    public async Task AssignTemplateToProject_ReadsButNeverWritesRunsOrPackages()
    {
        var context = new StudioContext();
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var run = WorkflowRun.Start(
            "run-active",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            scheme.GetRequiredStage(scheme.InitialStageId),
            Now);

        context.Runs.ActiveRun = run;
        context.Packages.Packages = new[]
        {
            CreatePackage("pkg-1", Hash('a')),
            CreatePackage("pkg-2", Hash('b'))
        };

        var result = await context.Studio.AssignTemplateToProjectAsync(
            "project-1",
            WorkflowStudioService.StandardTemplateId,
            1);

        Assert.True(result.IsAssigned);
        Assert.NotNull(result.AssignmentId);
        Assert.Equal("run-active", result.ActiveRunId);
        Assert.Equal(scheme.InitialStageId, result.ActiveRunStageId);
        Assert.Equal(
            new[] { Hash('a'), Hash('b') }.OrderBy(hash => hash, StringComparer.Ordinal),
            result.ImportedPackageHashes);

        // The fakes fail the test if the studio ever writes; reference equality proves the run and the
        // imported package were not replaced either.
        Assert.Same(run, context.Runs.ActiveRun);
        Assert.Equal("pkg-1", context.Packages.Packages[0].Id);
        Assert.Equal(Hash('a'), context.Packages.Packages[0].OriginalHash);
    }

    [Fact]
    public async Task DocumentDraft_UpdateChangesHashAndVersionAndResetsEveryDecision()
    {
        var context = new StudioContext();
        var draft = await context.Documents.GenerateDraftAsync(
            DocumentTemplateKind.ProblemStatement,
            "Проблема");

        var originalHash = draft.ContentHash;

        await context.Documents.AddReviewVerdictAsync(
            draft.DraftId,
            CreateVerdict("Reviewer", originalHash, WorkflowReviewVerdict.Approve));
        await context.Documents.ApproveDraftAsync(draft.DraftId, CreateApproval(originalHash));

        Assert.True(draft.IsApprovedForCoding);
        Assert.Single(draft.ReviewerVerdicts);

        await context.Documents.UpdateDraftAsync(draft.DraftId, "новый текст без секций");

        Assert.Equal(2, draft.Version);
        Assert.NotEqual(originalHash, draft.ContentHash);
        Assert.Empty(draft.ReviewerVerdicts);
        Assert.Null(draft.UserApproval);
        Assert.False(draft.IsApprovedForCoding);
    }

    [Fact]
    public async Task AddReviewVerdict_ForSupersededHash_IsRejected()
    {
        var context = new StudioContext();
        var draft = await context.Documents.GenerateDraftAsync(
            DocumentTemplateKind.Architecture,
            "Архитектура");

        var staleHash = Hash('e');

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => context.Documents.AddReviewVerdictAsync(
                draft.DraftId,
                CreateVerdict("Reviewer", staleHash, WorkflowReviewVerdict.Approve)));
    }

    [Fact]
    public async Task EvaluateCompleteness_DetectsMissingRequiredSections()
    {
        var context = new StudioContext();
        var complete = await context.Documents.GenerateDraftAsync(
            DocumentTemplateKind.Roadmap,
            "Дорожная карта");

        var completeEvaluation = context.Documents.EvaluateCompleteness(complete.DraftId);

        Assert.True(completeEvaluation.AllRequiredSectionsPresent);
        Assert.Empty(completeEvaluation.MissingSections);
        Assert.NotEmpty(completeEvaluation.CompletenessCriteria);

        var incomplete = await context.Documents.GenerateDraftAsync(
            DocumentTemplateKind.Roadmap,
            "Черновик",
            initialContent: "## Этапы\n\nтолько один раздел");
        var incompleteEvaluation = context.Documents.EvaluateCompleteness(incomplete.DraftId);

        Assert.False(incompleteEvaluation.AllRequiredSectionsPresent);
        Assert.Contains("Вехи", incompleteEvaluation.MissingSections);
    }

    [Fact]
    public async Task PreSendPreview_WithSecretFinding_IsBlockedAndRedacted()
    {
        var context = new StudioContext();
        const string syntheticSecret = "synthetic-secret-for-preview-regression-4902";
        const string originalContent = "# Problem\n\napi_key=" + syntheticSecret + "\n\nUnflagged explanation.";

        context.Scanner.Report = new WorkflowSecretScanReport(
            true,
            new[] { new WorkflowSecretFinding("drafts/ProblemStatement.md", 3, "TestSecret", "[redacted]") },
            new[] { "drafts/ProblemStatement.md" });

        var draft = await context.Documents.GenerateDraftAsync(
            DocumentTemplateKind.ProblemStatement,
            "Проблема",
            initialContent: originalContent);

        var preview = await context.Documents.GeneratePreSendPreviewAsync(draft.DraftId);

        Assert.True(preview.HasScanner);
        Assert.True(preview.IsBlocked);
        Assert.True(preview.ScanReport.HasFindings);
        Assert.Single(preview.RedactedLines);
        Assert.Contains("[REDACTED:TestSecret]", preview.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticSecret, preview.Content, StringComparison.Ordinal);
        Assert.Contains("Unflagged explanation.", preview.Content, StringComparison.Ordinal);
        Assert.Equal(originalContent, draft.Content);
        Assert.Equal(originalContent, context.Documents.GetRequiredDraft(draft.DraftId).Content);
    }

    [Fact]
    public async Task PreSendPreview_WithoutScanner_IsBlockedFailClosed()
    {
        var context = new StudioContext();
        var withoutScanner = new DocumentTemplateService(
            secretScanner: null,
            context.Time,
            () => "draft-no-scanner");

        var draft = await withoutScanner.GenerateDraftAsync(
            DocumentTemplateKind.Architecture,
            "Архитектура");

        var preview = await withoutScanner.GeneratePreSendPreviewAsync(draft.DraftId);

        Assert.False(preview.HasScanner);
        Assert.True(preview.IsBlocked);
        Assert.Empty(preview.Content);
    }

    [Fact]
    public void Gate_BlocksMissingRequiredDocument()
    {
        var context = new StudioContext();
        var documents = new[] { CreateGateDocument(DocumentTemplateKind.ProblemStatement) };

        var result = context.Gate.Evaluate(CreateGateRequest(documents));

        Assert.False(result.IsAllowed);
        Assert.True(result.HasMissingRequiredDocument);
        Assert.Equal(WorkflowScheme.CodeAndUiStageId, result.BlockedTransitionId);
        Assert.Contains("missing required document", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_BlocksMissingReviewer()
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve)
            });

        var result = context.Gate.Evaluate(CreateGateRequest(new[] { document }));

        Assert.False(result.IsAllowed);
        Assert.True(result.HasMissingReviewer);
        Assert.False(result.HasConflictingVerdicts);
        Assert.False(result.IsHashMismatch);
    }

    [Theory]
    [InlineData(WorkflowReviewVerdict.Reject)]
    [InlineData(WorkflowReviewVerdict.RequestChanges)]
    public void Gate_BlocksUnanimousNegativeReviewEvenWhenUserApproved(WorkflowReviewVerdict verdict)
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            approval: CreateApproval(Hash('1')),
            verdicts: WorkflowStudioDocumentRules.RequiredReviewerRoles
                .Select(role => CreateVerdict(role, Hash('1'), verdict)).ToArray());

        var result = context.Gate.Evaluate(CreateGateRequest(
            new[] { document }, new[] { DocumentTemplateKind.ProblemStatement }));

        Assert.False(result.IsAllowed);
        Assert.False(result.IsCheckSatisfied(PreCoderGateCheckIds.ReviewerUnanimity));
        Assert.False(result.HasConflictingVerdicts);
        Assert.NotEmpty(result.BlockingReasons);
    }

    [Fact]
    public void Gate_BlocksConflictingApproveRejectPair()
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            approval: CreateApproval(Hash('1')),
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Reject),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("UiReviewer", Hash('1'), WorkflowReviewVerdict.Approve)
            });

        var result = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            escalationTargetNodeId: "stage-conflict-resolution"));

        Assert.False(result.IsAllowed);
        Assert.True(result.HasConflictingVerdicts);
        Assert.True(result.RequiresEscalation);
        Assert.Equal("stage-conflict-resolution", result.EscalationTargetNodeId);
        Assert.Contains("stage-conflict-resolution", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_BlocksHashChangedAfterReview()
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            contentHash: Hash('2'),
            approval: CreateApproval(Hash('1')),
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("UiReviewer", Hash('1'), WorkflowReviewVerdict.Approve)
            });

        var result = context.Gate.Evaluate(CreateGateRequest(new[] { document }));

        Assert.False(result.IsAllowed);
        Assert.True(result.IsHashMismatch);
        Assert.True(result.HasMissingUserApproval);
        Assert.Contains("hash changed after review", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_BlocksMissingUserApproval()
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("UiReviewer", Hash('1'), WorkflowReviewVerdict.Approve)
            });

        var result = context.Gate.Evaluate(CreateGateRequest(new[] { document }));

        Assert.False(result.IsAllowed);
        Assert.True(result.HasMissingUserApproval);
        Assert.False(result.HasMissingReviewer);
    }

    [Fact]
    public void Gate_BlocksMissingUiArtifactAndVisualAcceptance()
    {
        var context = new StudioContext();
        var document = CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            approval: CreateApproval(Hash('1')),
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("UiReviewer", Hash('1'), WorkflowReviewVerdict.Approve)
            });

        var result = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            requiresUiArtifact: true,
            requiresUserVisualAcceptance: true));

        Assert.False(result.IsAllowed);
        Assert.True(result.HasMissingUiArtifact);
        Assert.True(result.HasMissingVisualAcceptance);
    }

    [Fact]
    public void Gate_BlocksMissingOrMismatchingRoute()
    {
        var context = new StudioContext();
        var document = CreateApprovedGateDocument();

        var singleKind = new[] { DocumentTemplateKind.ProblemStatement };

        var missingObserved = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            requiredDocumentKinds: singleKind,
            requestedRouteId: "route-star-cliproxy-agy"));
        var mismatched = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            requiredDocumentKinds: singleKind,
            requestedRouteId: "route-star-cliproxy-agy",
            observedRouteId: "route-star-cliproxy-codex"));
        var matched = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            requiredDocumentKinds: singleKind,
            requestedRouteId: "route-star-cliproxy-agy",
            observedRouteId: "route-star-cliproxy-agy"));

        Assert.True(missingObserved.IsRouteMismatch);
        Assert.True(mismatched.IsRouteMismatch);
        Assert.False(matched.IsRouteMismatch);
        Assert.True(matched.IsAllowed);
    }

    [Fact]
    public void Gate_AllowsOnlyWhenEveryCheckIsSatisfied()
    {
        var context = new StudioContext();
        var document = CreateApprovedGateDocument(hasUiArtifact: true, hasVisualAcceptance: true);

        var result = context.Gate.Evaluate(CreateGateRequest(
            new[] { document },
            requiredDocumentKinds: new[] { DocumentTemplateKind.ProblemStatement },
            requestedRouteId: "route-opencode",
            observedRouteId: "route-opencode",
            requiresUiArtifact: true,
            requiresUserVisualAcceptance: true));

        Assert.True(result.IsAllowed);
        Assert.All(result.Checks, check => Assert.True(check.IsSatisfied, check.CheckId));
        Assert.True(result.IsCheckSatisfied(PreCoderGateCheckIds.ReviewerUnanimity));
        Assert.True(result.IsCheckSatisfied(PreCoderGateCheckIds.HashIntegrity));
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public async Task DomainScenario_TaskToAcceptance_IsDeterministicAndGateGuarded()
    {
        var context = new StudioContext();
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var startedAt = Now;

        var run = WorkflowRun.Start(
            "run-e2e",
            "project-e2e",
            "pkg-e2e",
            "ver-e2e",
            "session-e2e",
            scheme.GetRequiredStage(scheme.InitialStageId),
            startedAt);

        // 1. Documents: задача → архитектура → ТЗ → roadmap, each reviewed by three separate models.
        var drafts = new List<WorkflowDocumentDraft>();
        var minute = 1;

        foreach (var kind in WorkflowStudioDocumentRules.RequiredDocumentKinds)
        {
            var draft = await context.Documents.GenerateDraftAsync(
                kind,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Draft {kind}"));
            var hash = draft.ContentHash;

            foreach (var role in WorkflowStudioDocumentRules.RequiredReviewerRoles)
            {
                await context.Documents.AddReviewVerdictAsync(
                    draft.DraftId,
                    CreateVerdict(role, hash, WorkflowReviewVerdict.Approve, startedAt.AddMinutes(minute)));
                minute++;
            }

            await context.Documents.ApproveDraftAsync(draft.DraftId, CreateApproval(hash));
            drafts.Add(draft);
        }

        // 2. Pre-coder gate: every required document is approved on its current hash and the route matches.
        var gateDocuments = drafts
            .Select(draft => new PreCoderGateDocument(
                draft.Kind,
                draft.DraftId,
                draft.ContentHash,
                draft.Version,
                draft.ReviewerVerdicts,
                draft.UserApproval,
                HasUiArtifact: true,
                HasVisualAcceptance: true))
            .ToArray();

        var preCoderGate = context.Gate.Evaluate(CreateGateRequest(
            gateDocuments,
            requiredDocumentKinds: drafts.Select(draft => draft.Kind).ToArray(),
            requestedRouteId: "route-opencode",
            observedRouteId: "route-opencode",
            requiresUiArtifact: true,
            requiresUserVisualAcceptance: true));

        Assert.True(preCoderGate.IsAllowed);

        // 3. The run walks the named stages task → … → code/UI → tests → acceptance. Every stage of the
        // standard scheme is gated by the artifact it recorded, and the reviews and the approval pin the
        // artifact of the stage they belong to.
        var documentHash = StageHash(WorkflowScheme.DocumentReviewStageId);
        var implementationHash = StageHash(WorkflowScheme.CodeAndUiStageId);
        var reviewHash = StageHash(WorkflowScheme.MultiLevelReviewStageId);
        var acceptanceHash = StageHash(WorkflowScheme.TestsAndUiAcceptanceStageId);

        run = Advance(scheme, run, WorkflowScheme.TaskSpecificationStageId, "problem statement delivered", minute++);
        run = Advance(scheme, run, WorkflowScheme.ArchitectureStageId, "architecture delivered", minute++);
        run = Advance(scheme, run, WorkflowScheme.TechnicalSpecificationStageId, "ТЗ delivered", minute++);
        run = Advance(scheme, run, WorkflowScheme.RoadmapStageId, "roadmap delivered", minute++);

        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.ReviewerRole,
            documentHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));
        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.ArchitectRole,
            documentHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));
        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.UiReviewerRole,
            documentHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));

        run = Advance(scheme, run, WorkflowScheme.DocumentReviewStageId, "three separate reviews approved", minute++);

        run.RecordUserApproval(new UserApprovalEvidence(
            "approval-run-1",
            "user",
            WorkflowScheme.UserApprovalStageId,
            StageHash(WorkflowScheme.UserApprovalStageId),
            UserApprovalDecision.Approved,
            "Утверждено",
            startedAt.AddMinutes(minute++)));

        run = Advance(scheme, run, WorkflowScheme.UserApprovalStageId, "user approved the current hash", minute++);
        run = Advance(scheme, run, WorkflowScheme.ImplementationPackagesStageId, "task packets delivered", minute++);

        Assert.Equal(WorkflowScheme.CodeAndUiStageId, run.CurrentStageId);
        Assert.True(preCoderGate.IsAllowed);

        // 4. Код/UI → многоуровневое ревью → тесты/приёмка → итог.
        run = Advance(scheme, run, WorkflowScheme.CodeAndUiStageId, "implementation diff delivered", minute++);

        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.ReviewerRole,
            reviewHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));
        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.UiReviewerRole,
            reviewHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));

        run = Advance(scheme, run, WorkflowScheme.MultiLevelReviewStageId, "multi-level review approved", minute++);

        run.RecordLegacyUnlinkedReviewerVerdict(CreateVerdict(
            WorkflowScheme.TesterRole,
            acceptanceHash,
            WorkflowReviewVerdict.Approve,
            startedAt.AddMinutes(minute++)));
        run.RecordUserApproval(new UserApprovalEvidence(
            "approval-run-2",
            "user",
            WorkflowScheme.TestsAndUiAcceptanceStageId,
            acceptanceHash,
            UserApprovalDecision.Approved,
            "Визуальная приёмка подтверждена",
            startedAt.AddMinutes(minute++)));

        run = Advance(
            scheme,
            run,
            WorkflowScheme.TestsAndUiAcceptanceStageId,
            "tests and visual acceptance passed",
            minute++);

        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, run.CurrentStageId);
        Assert.Equal(
            implementationHash,
            run.Artifacts.Single(artifact => artifact.StageId == WorkflowScheme.CodeAndUiStageId).HashSha256);

        // The last gated stage's artifact is what the final transition names, so the walk ended on evidence
        // and not on a hash somebody supplied.
        Assert.Equal(
            acceptanceHash,
            run.Transitions[^1].AuthorizingArtifactHash);

        run.Complete("Все документы, ревью и приёмка пройдены", startedAt.AddMinutes(minute));

        Assert.Equal(WorkflowRunState.Completed, run.State);
        Assert.Equal(WorkflowTerminalOutcome.Completed, run.TerminalOutcome);
        Assert.Equal(10, run.Transitions.Count);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public async Task SwitchAgyProfile_RefusesWithTheNamedMissingProofAndCarriesNoSessionOrCredentials()
    {
        var context = new StudioContext();
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true);

        var result = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy",
                MirasimState: mirasim));

        // The request is well formed, so the refusal is the missing native-switch proof, not a bad input.
        Assert.False(result.IsSwitched);
        Assert.True(result.IsBlocked);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);

        // No locally generated native session and no caller-vouched observed route.
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal("none", result.RouteEvidenceSource);
        Assert.Equal("route-star-cliproxy-agy", result.RequestedRouteId);
        Assert.Equal("refused-missing-native-switch-proof", result.Provenance);

        // The previous session is preserved, never carried into a fork.
        Assert.Equal("native-old", result.PreviousNativeSessionId);
        Assert.True(result.RequiresNewNativeSession);
        Assert.False(result.CarriesPreviousSession);
        Assert.False(result.CredentialsTransferred);

        // The refusal names the missing executable and the missing independent identity/session contract.
        Assert.Contains("agy-profile executable", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("star-cliproxy.exe", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("no provider, account, actual model or unique route key", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("no native session is created", result.FailureReason!, StringComparison.Ordinal);

        // The Mirasim snapshot the caller handed in comes back by reference and unmodified.
        Assert.Same(mirasim, result.MirasimState);
    }

    [Fact]
    public async Task SwitchCodexHome_RefusesWithTheNamedMissingProofAndKeepsMirasimUntouched()
    {
        var context = new StudioContext();
        var codexHome = Path.Combine(Path.GetTempPath(), "codex-home-alpha");
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-2", "relay-idle", false);

        var result = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account-alpha",
                PreviousNativeSessionId: "native-codex-old",
                CodexHomePath: codexHome,
                RequestedRouteId: "route-star-cliproxy-codex",
                MirasimState: mirasim));

        Assert.False(result.IsSwitched);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.Equal("none", result.RouteEvidenceSource);
        Assert.Equal("route-star-cliproxy-codex", result.RequestedRouteId);
        Assert.False(result.CredentialsTransferred);
        Assert.Same(mirasim, result.MirasimState);

        // The refusal text is allowed to name the missing Codex executable but never the CODEX_HOME path.
        Assert.Contains("star-cliproxy.exe", result.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(codexHome, result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchAccountContext_AWellFormedRequestCannotBeEchoedIntoAnObservedRouteOrLocalSession()
    {
        // The former proof: a caller repeated the requested route as the "observed" one and the service
        // accepted the echo. There is no longer any field a caller can fill to produce that acceptance, so
        // the negative control asserts the shape of the request instead of a false success.
        var request = new WorkflowAccountContextSwitchRequest(
            AccountContextKind.Agy,
            "profile-alpha",
            PreviousNativeSessionId: "native-old",
            AgyProfileName: "profile-alpha",
            RequestedRouteId: "route-star-cliproxy-agy",
            MirasimState: new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true));

        var observedRouteParameter = typeof(WorkflowAccountContextSwitchRequest)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => string.Equals(
                parameter.Name,
                "ObservedRouteId",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(observedRouteParameter);

        var context = new StudioContext();
        var result = await context.Studio.SwitchAccountContextAsync(request);

        Assert.False(result.IsSwitched);
        Assert.Null(result.ObservedRouteId);
        Assert.Null(result.NativeSessionId);
        Assert.Equal(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
    }

    [Fact]
    public async Task SwitchAccountContext_HasNoLocallyGeneratedNativeSessionSource()
    {
        // The second half of the former proof: the service generated the "native" session id in-process.
        // Removing the factory removes the only way a native session id could be invented here, and a
        // composition can no longer be handed one.
        var constructors = typeof(WorkflowStudioService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(Func<string>))
            .ToArray();

        Assert.Empty(constructors);

        var context = new StudioContext();
        var agy = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy"));

        var codex = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account",
                PreviousNativeSessionId: "native-old",
                CodexHomePath: Path.Combine(Path.GetTempPath(), "codex-home-negation"),
                RequestedRouteId: "route-star-cliproxy-codex"));

        Assert.Null(agy.NativeSessionId);
        Assert.Null(codex.NativeSessionId);
    }

    [Fact]
    public async Task SwitchAccountContext_InvalidContext_IsDistinguishedFromTheMissingProof()
    {
        var context = new StudioContext();

        var invalidProfile = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile alpha!",
                AgyProfileName: "profile alpha!",
                RequestedRouteId: "route-star-cliproxy-agy"));

        var relativeHome = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account",
                CodexHomePath: "relative/codex-home",
                RequestedRouteId: "route-star-cliproxy-codex"));

        Assert.False(invalidProfile.IsSwitched);
        Assert.Equal(WorkflowAccountContextSwitchRefusal.InvalidContext, invalidProfile.Refusal);
        Assert.Null(invalidProfile.NativeSessionId);
        Assert.False(relativeHome.IsSwitched);
        Assert.Equal(WorkflowAccountContextSwitchRefusal.InvalidContext, relativeHome.Refusal);
        Assert.Null(relativeHome.NativeSessionId);

        // Each reason names its own kind instead of borrowing the other one's wording.
        Assert.Contains("AGY account context", invalidProfile.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("agy-profile", invalidProfile.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("Codex account context", relativeHome.FailureReason!, StringComparison.Ordinal);

        // The invalid-input reason never repeats the path the caller supplied.
        Assert.DoesNotContain("relative/codex-home", relativeHome.FailureReason!, StringComparison.Ordinal);

        // An unusable context is not a missing-proof refusal, and a missing proof is not an unusable one.
        Assert.NotEqual(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            invalidProfile.Refusal);
        Assert.NotEqual(
            WorkflowAccountContextSwitchRefusal.InvalidContext,
            (await context.Studio.SwitchAccountContextAsync(
                new WorkflowAccountContextSwitchRequest(
                    AccountContextKind.Agy,
                    "profile-alpha",
                    AgyProfileName: "profile-alpha",
                    RequestedRouteId: "route-star-cliproxy-agy"))).Refusal);
    }

    [Fact]
    public async Task SwitchAccountContext_CancelledRequest_IsDistinguishedAndTouchesNothing()
    {
        var context = new StudioContext();
        var mirasim = new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy",
                MirasimState: mirasim),
            cancellation.Token);

        Assert.False(result.IsSwitched);
        Assert.Equal(WorkflowAccountContextSwitchRefusal.Cancelled, result.Refusal);
        Assert.Null(result.NativeSessionId);
        Assert.Null(result.ObservedRouteId);
        Assert.Same(mirasim, result.MirasimState);
        Assert.NotEqual(
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            result.Refusal);
        Assert.NotEqual(
            WorkflowAccountContextSwitchRefusal.InvalidContext,
            result.Refusal);
    }

    [Fact]
    public async Task SwitchAccountContext_WritesNoSessionExecutionRunOrPackageState()
    {
        // The refusal has to happen before the first side effect. The run and package stores behind the
        // studio refuse every write and count the attempt, so a refused switch is proven to have written
        // nothing rather than merely to have produced a result.
        var context = new StudioContext();
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var activeRun = WorkflowRun.Start(
            "run-active",
            "project-alpha",
            "pkg-alpha",
            "ver-alpha",
            "session-active",
            scheme.Stages[0],
            DateTimeOffset.UnixEpoch);
        context.Runs.ActiveRun = activeRun;

        var agy = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Agy,
                "profile-alpha",
                PreviousNativeSessionId: "native-old",
                AgyProfileName: "profile-alpha",
                RequestedRouteId: "route-star-cliproxy-agy",
                MirasimState: new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true)));

        var codex = await context.Studio.SwitchAccountContextAsync(
            new WorkflowAccountContextSwitchRequest(
                AccountContextKind.Codex,
                "codex-account",
                PreviousNativeSessionId: "native-old",
                CodexHomePath: Path.Combine(Path.GetTempPath(), "codex-home-no-write"),
                RequestedRouteId: "route-star-cliproxy-codex",
                MirasimState: new WorkflowMirasimIsolationState("mirasim-account-1", "relay-active", true)));

        Assert.False(agy.IsSwitched);
        Assert.False(codex.IsSwitched);
        Assert.Equal(0, context.Runs.SaveCalls);
        Assert.Equal(0, context.Packages.UpsertCalls);

        // The active run is still the one the studio was holding, untouched.
        Assert.Same(activeRun, context.Runs.ActiveRun);
        Assert.Equal("run-active", context.Runs.ActiveRun.Id);
    }

    private static WorkflowRun Advance(
        WorkflowScheme scheme,
        WorkflowRun run,
        string currentStageId,
        string reason,
        int minute)
    {
        Assert.Equal(currentStageId, run.CurrentStageId);

        var currentStage = scheme.GetRequiredStage(currentStageId);
        var nextStage = scheme.GetRequiredStage(currentStage.NextStageId!);

        // Every stage of the standard scheme is gated by a stored artifact, so the walk records the content it
        // produced before it moves on. The artifact lands on a copy of the run, which is the one that advances.
        run = run.WithArtifact(CreateArtifact(run.Id, currentStage, minute));

        run.AdvanceTo(currentStage, nextStage, reason, Now.AddMinutes(minute));

        return run;
    }

    private static WorkflowArtifactEvidence CreateArtifact(
        string runId,
        WorkflowStageDefinition stage,
        int minute) =>
        new(
            $"artifact-{stage.StageId}",
            runId,
            stage.StageId,
            stage.ArtifactRequirement!,
            StageHash(stage.StageId),
            StageHash(stage.StageId),
            Now.AddMinutes(minute),
            128,
            DataClassification.PrivateSource);

    /// <summary>The hash of the artifact a stage recorded, so the walk's verdicts can pin the same content.</summary>
    private static string StageHash(string stageId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"artifact of '{stageId}'");
        var digest = System.Security.Cryptography.SHA256.HashData(bytes);

        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static PreCoderGateRequest CreateGateRequest(
        IReadOnlyList<PreCoderGateDocument> documents,
        IReadOnlyList<DocumentTemplateKind>? requiredDocumentKinds = null,
        string? requestedRouteId = null,
        string? observedRouteId = null,
        bool requiresUiArtifact = false,
        bool requiresUserVisualAcceptance = false,
        string? escalationTargetNodeId = null) =>
        new(
            requiredDocumentKinds ?? WorkflowStudioDocumentRules.RequiredDocumentKinds,
            WorkflowStudioDocumentRules.RequiredReviewerRoles,
            documents,
            WorkflowScheme.CodeAndUiStageId,
            requestedRouteId,
            observedRouteId,
            requiresUiArtifact,
            requiresUserVisualAcceptance,
            escalationTargetNodeId);

    private static PreCoderGateDocument CreateApprovedGateDocument(
        bool hasUiArtifact = false,
        bool hasVisualAcceptance = false) =>
        CreateGateDocument(
            DocumentTemplateKind.ProblemStatement,
            verdicts: new[]
            {
                CreateVerdict("Reviewer", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("Architect", Hash('1'), WorkflowReviewVerdict.Approve),
                CreateVerdict("UiReviewer", Hash('1'), WorkflowReviewVerdict.Approve)
            },
            approval: CreateApproval(Hash('1')),
            hasUiArtifact: hasUiArtifact,
            hasVisualAcceptance: hasVisualAcceptance);

    private static PreCoderGateDocument CreateGateDocument(
        DocumentTemplateKind kind,
        string? contentHash = null,
        IReadOnlyList<ReviewerVerdictRecord>? verdicts = null,
        UserApprovalEvidence? approval = null,
        bool hasUiArtifact = false,
        bool hasVisualAcceptance = false) =>
        new(
            kind,
            "draft-" + kind,
            contentHash ?? Hash('1'),
            1,
            verdicts ?? Array.Empty<ReviewerVerdictRecord>(),
            approval,
            hasUiArtifact,
            hasVisualAcceptance);

    private static ReviewerVerdictRecord CreateVerdict(
        string role,
        string hash,
        WorkflowReviewVerdict verdict,
        DateTimeOffset? recordedAtUtc = null) =>
        new(
            role,
            "route-opencode",
            hash,
            verdict,
            "evidence",
            recordedAtUtc ?? Now);

    private static UserApprovalEvidence CreateApproval(string hash) =>
        new(
            "approval-" + hash[^4..],
            "user",
            WorkflowScheme.UserApprovalStageId,
            hash,
            UserApprovalDecision.Approved,
            "Утверждено",
            Now);

    private static WorkflowPackage CreatePackage(string id, string hash) =>
        new(
            id,
            "Package " + id,
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            hash,
            hash,
            Now,
            Now);

    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private sealed class StudioContext
    {
        private int _draftSequence;

        public StudioContext()
        {
            Time = new FixedTimeProvider(Now);
            Runs = new FakeWorkflowRunRepository();
            Packages = new FakeWorkflowPackageRepository();
            Scanner = new FakeWorkflowSecretScanner();

            Studio = new WorkflowStudioService(
                graphValidator: new WorkflowGraphValidator(),
                templateStore: new InMemoryWorkflowTemplateStore(),
                timeProvider: Time,
                runRepository: Runs,
                packageRepository: Packages);

            Documents = new DocumentTemplateService(
                Scanner,
                Time,
                () => "draft-" + (++_draftSequence),
                new TestSupport.FixedUserApprovalIdentity("synthetic-studio-user"));
        }

        public FixedTimeProvider Time { get; }

        public FakeWorkflowRunRepository Runs { get; }

        public FakeWorkflowPackageRepository Packages { get; }

        public FakeWorkflowSecretScanner Scanner { get; }

        public WorkflowStudioService Studio { get; }

        public DocumentTemplateService Documents { get; }

        public PreCoderGateValidator Gate { get; } = new();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeWorkflowRunRepository : IWorkflowRunRepository
    {
        public WorkflowRun? ActiveRun { get; set; }

        /// <summary>Counts any write attempt so a refused switch can be proven to have made none.</summary>
        public int SaveCalls { get; private set; }

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        {
            SaveCalls++;

            return Task.FromException(new InvalidOperationException(
                "The Workflow Studio must never save or mutate a workflow run."));
        }

        public Task SaveArtifactAsync(
            WorkflowRun run,
            WorkflowArtifactEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;

            return Task.FromException(new InvalidOperationException(
                "The Workflow Studio must never save or mutate a workflow run."));
        }

        public Task<WorkflowRun?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            var active = ActiveRun;

            return Task.FromResult(active is not null && active.Id == id ? active : null);
        }

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            var active = ActiveRun;

            return Task.FromResult<IReadOnlyList<WorkflowRun>>(
                active is not null && active.ProjectId == projectId
                    ? new[] { active }
                    : Array.Empty<WorkflowRun>());
        }

        public Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            var active = ActiveRun;

            return Task.FromResult(active is not null && active.ProjectId == projectId ? active : null);
        }
    }

    private sealed class FakeWorkflowPackageRepository : IWorkflowPackageRepository
    {
        public IReadOnlyList<WorkflowPackage> Packages { get; set; } = Array.Empty<WorkflowPackage>();

        /// <summary>Counts any write attempt so a refused switch can be proven to have made none.</summary>
        public int UpsertCalls { get; private set; }

        public Task UpsertAsync(WorkflowPackage package, CancellationToken cancellationToken = default)
        {
            UpsertCalls++;

            return Task.FromException(new InvalidOperationException(
                "The Workflow Studio must never mutate an imported workflow package."));
        }

        public Task<WorkflowPackage?> GetByIdAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Packages.FirstOrDefault(package => package.Id == id));

        public Task<WorkflowPackage?> GetByOriginalHashAsync(
            string originalHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Packages.FirstOrDefault(package => package.OriginalHash == originalHash));

        public Task<IReadOnlyList<WorkflowPackage>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Packages);

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new InvalidOperationException(
                "The Workflow Studio must never delete an imported workflow package."));
    }

    private sealed class FakeWorkflowSecretScanner : IWorkflowSecretScanner
    {
        public WorkflowSecretScanReport Report { get; set; } = WorkflowSecretScanReport.Empty;

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The document preview only scans in-memory drafts.");

        public Task<WorkflowSecretScanReport> ScanFilesAsync(
            IReadOnlyDictionary<string, byte[]> files,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);
    }
}

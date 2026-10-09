using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Evidence for the product user approval of a run's current pinned stage artifact (ROADMAP 10J).
/// <para>
/// The whole path runs against the real composition over a migrated SQLite database: the shipped Workflows
/// screen, the registered run service, the registered blob store and the registered Windows logon identity.
/// Every assertion is about stored rows or about the real controls of the real loaded
/// <c>ScreenTemplates.xaml</c>, never about a view model that was handed its own object.
/// </para>
/// <para>
/// What these tests pin down is the negative space as much as the happy path: no decision is offered
/// without a deliberate comment, nothing is offered when the committed bytes cannot be re-hashed, a hash
/// that was replaced or a run that was replaced is refused by name, and the approver that is persisted is
/// the current Windows logon of this process and nothing the caller supplied.
/// </para>
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowRunUserApprovalCommandTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionOneId = "version-1";
    private const string VersionTwoId = "version-2";
    private const string TemplateId = "approval-template";
    private const string ApprovalStageId = "node-a";
    private const string ArtifactKind = "ApprovedDocument";

    /// <summary>A name no real decision may be recorded under, used to prove a forged one is replaced.</summary>
    private const string ForgedApprover = "studio-user";

    private static readonly DateTimeOffset SeededAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "llmworkgui-phase10j-" + Guid.NewGuid().ToString("N"));

    private readonly string _appData;

    private readonly SqliteConnectionFactory _factory;

    public WorkflowRunUserApprovalCommandTests()
    {
        _appData = Path.Combine(_root, "appdata");
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));

        using var host = CreateHost();
        HostBootstrapper.InitializeAsync(host).GetAwaiter().GetResult();
        SeedAsync(host.Services).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var directory in new[] { _appData, _root })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory must never turn an approval assertion red.
            }
        }
    }

    [Fact]
    public async Task TheDeliberateApprovalIsRecordedAgainstTheStoredHashByTheCurrentWindowsLogon()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);

        var artifact = Assert.Single(library.ObservedRun!.Artifacts);
        var expectedApprover = WindowsIdentity.GetCurrent().Name;

        Assert.False(string.IsNullOrWhiteSpace(expectedApprover));

        library.UserApprovalComment = "The document satisfies the acceptance criteria.";
        Assert.True(library.CanApproveObservedArtifact);
        Assert.True(library.ApproveObservedArtifactCommand.CanExecute(null));

        await library.ApproveObservedArtifactAsync();

        Assert.Empty(library.Blocker);
        Assert.Contains("Approved", library.StatusMessage, StringComparison.Ordinal);

        // The decision is a stored row, it pins the hash of the committed bytes, and the approver is the
        // logon of this process rather than anything the caller could have named.
        var stored = await StoredRunAsync(host.Services, library.ObservedRun!.Id);
        var approval = Assert.Single(stored.Approvals);

        Assert.Equal(ApprovalStageId, approval.StageId);
        Assert.Equal(artifact.HashSha256, approval.ArtifactHash);
        Assert.Equal(UserApprovalDecision.Approved, approval.Decision);
        Assert.Equal(expectedApprover, approval.ApprovedBy);
        Assert.Equal("The document satisfies the acceptance criteria.", approval.Comment);

        // The comment the operator typed is the one that was stored, and it is not the approver field.
        Assert.NotEqual(ForgedApprover, approval.ApprovedBy);
        Assert.NotEqual("operator", approval.ApprovedBy);
        Assert.DoesNotContain(stored.Approvals, other => string.Equals(other.ApprovedBy, ForgedApprover, StringComparison.Ordinal));

        // Every approval has its own id, so a repeated decision is a second record and never an overwrite.
        library.UserApprovalComment = "Still acceptable after a second look.";
        await library.ApproveObservedArtifactAsync();

        var again = await StoredRunAsync(host.Services, library.ObservedRun!.Id);
        Assert.Equal(2, again.Approvals.Count);
        Assert.Equal(2, again.Approvals.Select(approval => approval.ApprovalId).Distinct().Count());
    }

    [Fact]
    public async Task TheDeliberateRejectionEndsTheRunAndTheScreenSaysSo()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;

        library.UserApprovalComment = "The document is not acceptable.";
        Assert.True(library.CanRejectObservedArtifact);

        await library.RejectObservedArtifactAsync();

        Assert.Empty(library.Blocker);

        // The rejection is a terminal transition, and the screen states that instead of leaving the
        // operator to infer it from a panel that quietly went away.
        var stored = await StoredRunAsync(host.Services, runId);
        Assert.Equal(WorkflowRunState.Failed, stored.State);
        Assert.Equal(WorkflowTerminalOutcome.Rejected, stored.TerminalOutcome);
        Assert.Equal("The document is not acceptable.", stored.TerminalReason);

        Assert.Contains("Rejected", library.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("терминальное", library.RunCommandNotice, StringComparison.Ordinal);

        // A terminal run is no longer the project's active run, so the console stops observing it and offers
        // nothing further - without the screen claiming the rejection was refused.
        Assert.False(library.HasObservedRun);
        Assert.False(library.CanApproveObservedArtifact);
        Assert.False(library.CanRejectObservedArtifact);
        Assert.Single(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task NoDecisionIsOfferedOrRecordedWithoutADeliberateComment()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);

        // The run, its stage, its required kind and its stored hash are all established - and still nothing
        // is offered, because there is no default decision in either direction.
        Assert.True(library.CanDecideObservedUserApproval);
        Assert.False(library.CanApproveObservedArtifact);
        Assert.False(library.CanRejectObservedArtifact);
        Assert.False(library.ApproveObservedArtifactCommand.CanExecute(null));
        Assert.False(library.RejectObservedArtifactCommand.CanExecute(null));

        library.UserApprovalComment = "   ";
        Assert.False(library.CanApproveObservedArtifact);
        Assert.False(library.CanRejectObservedArtifact);

        // A direct invocation with an empty comment still fails closed, and it says why.
        await library.ApproveObservedArtifactAsync();

        Assert.Contains("комментарий", library.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(library.StatusMessage);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task NoDecisionIsOfferedWhenTheCommittedBytesAreGone()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);

        library.UserApprovalComment = "Looks acceptable.";
        Assert.True(library.CanApproveObservedArtifact);

        // The row stays exactly as it was; only the bytes behind it are gone.
        var artifact = Assert.Single(library.ObservedRun!.Artifacts);
        DeleteCommittedBlob(artifact.BlobId);

        await library.ObserveActiveRunAsync();

        Assert.False(library.CanApproveObservedArtifact);
        Assert.False(library.CanRejectObservedArtifact);
        Assert.Contains("не проверены", library.UserApprovalRequirementDisplay, StringComparison.Ordinal);

        await library.ApproveObservedArtifactAsync();

        Assert.Contains(artifact.ArtifactId, library.Blocker, StringComparison.Ordinal);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task AStageThatRequiresNoUserApprovalIsUnofferedByName()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignApprovalTemplateAsync(host.Services, requiresUserApproval: false, requiresArtifact: true);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        await AttachArtifactAsync(library, "a document nobody has to approve yet");

        library.UserApprovalComment = "Looks acceptable.";

        Assert.False(library.CanDecideObservedUserApproval);
        Assert.False(library.CanApproveObservedArtifact);
        Assert.Contains("не требует", library.UserApprovalRequirementDisplay, StringComparison.Ordinal);

        await library.ApproveObservedArtifactAsync();

        Assert.Contains("не требует", library.Blocker, StringComparison.Ordinal);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task AStageWithNoStoredArtifactIsUnofferedByName()
    {
        using var host = await CreateInitializedHostAsync();
        await AssignApprovalTemplateAsync(host.Services, requiresUserApproval: true, requiresArtifact: true);

        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        library.UserApprovalComment = "Looks acceptable.";

        Assert.False(library.CanDecideObservedUserApproval);
        Assert.Contains("записанный хеш: нет", library.UserApprovalRequirementDisplay, StringComparison.Ordinal);

        await library.ApproveObservedArtifactAsync();

        Assert.Contains("нет сохранённого артефакта", library.Blocker, StringComparison.Ordinal);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task AnApprovalAfterTheArtifactWasReplacedIsRefusedAndNothingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var first = Assert.Single(library.ObservedRun!.Artifacts);

        library.UserApprovalComment = "Looks acceptable.";
        Assert.True(library.CanApproveObservedArtifact);

        // The artifact is replaced after the screen established the target. The stored row and the committed
        // bytes are both new, and the decision the operator read is about the old ones.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        var second = await RecordReplacementAsync(runService, runId, "a different document nobody approved");

        Assert.NotEqual(first.HashSha256, second.HashSha256);

        await library.ApproveObservedArtifactAsync();

        Assert.Contains(second.HashSha256, library.Blocker, StringComparison.Ordinal);
        Assert.Contains(first.HashSha256, library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task AnApprovalForAReplacedRunIsRefusedAndNothingIsRecorded()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;

        library.UserApprovalComment = "Looks acceptable.";
        Assert.True(library.CanApproveObservedArtifact);

        var runService = host.Services.GetRequiredService<IWorkflowRunService>();
        await runService.CancelRunAsync(runId, "closed by an operator elsewhere");
        await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        await library.ApproveObservedArtifactAsync();

        Assert.Contains("Активный run проекта теперь", library.Blocker, StringComparison.Ordinal);
        Assert.Empty(library.StatusMessage);
        Assert.Empty(await StoredApprovalsAsync(host.Services));
    }

    [Fact]
    public async Task AnApprovalAlreadyPersistedIsNeverReportedAsRefusedWhenTheSelectionMovesDuringTheAwait()
    {
        var gate = new RunServiceGate();
        using var host = await CreateInitializedHostAsync(gate);
        var library = await StartApprovableRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var artifact = Assert.Single(library.ObservedRun!.Artifacts);

        library.UserApprovalComment = "The document is acceptable.";
        Assert.True(library.CanApproveObservedArtifact);

        gate.Arm();

        // Started, not awaited: the command suspends inside the gated run service and hands the dispatcher
        // back, which is what keeps the WPF binding pipeline alive while the selection moves underneath it.
        StaTestRunner.EnsureApplication();
        var start = StaTestRunner.Run(() => library.ApproveObservedArtifactAsync());

        await gate.Entered;

        StaTestRunner.Run(() => library.SelectedVersion =
            library.Versions.Single(version => version.Id == VersionOneId));
        gate.Open();

        await start;
        StaTestRunner.Run(DrainDispatcher);

        // The decision is stored, so the screen reports it as committed and measures the difference against
        // what was actually acted on - it never turns a persisted decision back into a refusal.
        Assert.Empty(library.Blocker);
        Assert.Contains("Решение записано", library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains(VersionTwoId, library.RunCommandNotice, StringComparison.Ordinal);
        Assert.Contains("Выбор на экране изменился", library.RunCommandNotice, StringComparison.Ordinal);

        var stored = await StoredRunAsync(host.Services, runId);
        var approval = Assert.Single(stored.Approvals);
        Assert.Equal(artifact.HashSha256, approval.ArtifactHash);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, approval.ApprovedBy);
    }

    [Fact]
    public async Task TheStoredApprovalAndItsApproverSurviveAReopenOfTheScreenAndTheDatabase()
    {
        string runId;

        using (var host = await CreateInitializedHostAsync())
        {
            var library = await StartApprovableRunAsync(host.Services);
            runId = library.ObservedRun!.Id;

            library.UserApprovalComment = "Approved once, for good.";
            await library.ApproveObservedArtifactAsync();

            await host.StopAsync();
        }

        // A brand new service provider and a brand new screen over the same database: nothing is carried
        // over in memory, so what is read below comes from storage alone.
        using var reopened = await CreateInitializedHostAsync();
        var reopenedLibrary = CreateLibrary(reopened.Services);
        await reopenedLibrary.RefreshAsync();

        var stored = await StoredRunAsync(reopened.Services, runId);
        var approval = Assert.Single(stored.Approvals);

        Assert.Equal(UserApprovalDecision.Approved, approval.Decision);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, approval.ApprovedBy);

        // And the reopened screen reports the same decision for the same stage, read from the run's own
        // approval history rather than from anything it kept in memory.
        Assert.Contains(
            approval.ApprovedBy,
            reopenedLibrary.UserApprovalRequirementDisplay,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheComposedWindowLabelsTheApproverAsTheUnsignedLocalWindowsLogonAndShowsIt()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);

        var approver = WindowsIdentity.GetCurrent().Name;
        var label = library.UserApprovalApproverDisplay;

        // The screen states exactly what the recorded name is - the logon of this Windows session - and
        // nothing stronger: no provider, no backend account, no signature.
        Assert.Contains(approver!, label, StringComparison.Ordinal);
        Assert.Contains("локальный вход Windows", label, StringComparison.Ordinal);
        Assert.Contains("без подписи", label, StringComparison.Ordinal);

        // And the shipped panel puts that exact sentence on screen.
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                Assert.Contains(label, ReadTextBlocks(root));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AForgedApproverNameIsReplacedByTheCurrentWindowsLogonAtTheServiceBoundary()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);
        var runId = library.ObservedRun!.Id;
        var artifact = Assert.Single(library.ObservedRun!.Artifacts);

        // The decision is handed to the composed service straight, with a name nobody is approved under and
        // without going anywhere near the screen: the service resolves the approver itself.
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();

        var recorded = await runService.RecordUserApprovalAsync(
            runId,
            new UserApprovalEvidence(
                "approval-forged",
                ForgedApprover,
                ApprovalStageId,
                artifact.HashSha256,
                UserApprovalDecision.Approved,
                "Approved without the screen.",
                DateTimeOffset.UtcNow));

        var approval = Assert.Single(recorded.Approvals);

        Assert.Equal("approval-forged", approval.ApprovalId);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, approval.ApprovedBy);
        Assert.DoesNotContain(
            recorded.Approvals,
            other => other.ApprovedBy.Contains(ForgedApprover, StringComparison.Ordinal));

        // And the screen, which never saw that name, reports the one that was actually stored.
        await library.ObserveActiveRunAsync();
        Assert.Contains(WindowsIdentity.GetCurrent().Name!, library.UserApprovalRequirementDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain(ForgedApprover, library.UserApprovalRequirementDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDecisionSurfacesCarryNoArtifactBytesNoLocalPathAndNoForgedName()
    {
        using var host = await CreateInitializedHostAsync();
        var library = CreateLibrary(host.Services);
        await AssignApprovalTemplateAsync(host.Services, requiresUserApproval: true, requiresArtifact: true);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        const string documentContent = "a document whose bytes must never be quoted back";
        var localPath = WriteTempFile(documentContent);
        library.StageArtifactPath = localPath;
        await library.AttachStageArtifactAsync();

        var runId = library.ObservedRun!.Id;
        var runService = host.Services.GetRequiredService<IWorkflowRunService>();

        // The artifact is replaced after the screen established its target, so the changed-hash refusal is
        // produced with a real local file in play.
        var replacement = await RecordReplacementAsync(runService, runId, "replacement content");

        library.UserApprovalComment = "Looks acceptable.";
        await library.ApproveObservedArtifactAsync();

        Assert.Contains(replacement.HashSha256, library.Blocker, StringComparison.Ordinal);
        AssertNoDetail(library.Blocker, localPath, documentContent, ForgedApprover, "operator", "studio-user");
        AssertNoDetail(library.StatusMessage, localPath, documentContent, ForgedApprover, "operator", "studio-user");

        // And with the committed bytes of the replacement removed, the refusal is the unverified one - and
        // still names no file of the operator's own.
        DeleteCommittedBlob(replacement.BlobId);
        await library.ObserveActiveRunAsync();
        await library.ApproveObservedArtifactAsync();

        Assert.False(string.IsNullOrWhiteSpace(library.Blocker));
        AssertNoDetail(library.Blocker, localPath, documentContent, ForgedApprover, "operator", "studio-user");

        // Neither refusal recorded a decision, and the run still points at the replacement.
        Assert.Empty(await StoredApprovalsAsync(host.Services));

        var stored = await StoredRunAsync(host.Services, runId);
        Assert.Equal(
            replacement.HashSha256,
            WorkflowArtifactEvidence.SelectCurrent(stored.Artifacts, runId, ApprovalStageId, ArtifactKind)!.HashSha256);
    }

    [Fact]
    public async Task TheShippedApprovalPanelIsCapturedWithItsVerifiedHashAndItsTwoActions()
    {
        using var host = await CreateInitializedHostAsync();
        var library = await StartApprovableRunAsync(host.Services);

        // A comment, so the capture shows the state the decision is actually made in: both actions live, the
        // hash on screen, and the approver named as what it is.
        library.UserApprovalComment = "The document satisfies the acceptance criteria.";

        var artifact = Assert.Single(library.ObservedRun!.Artifacts);
        var approver = WindowsIdentity.GetCurrent().Name!;

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            // Captured in the ordinary shipped window. The run panel, the artifact panel and the approval
            // panel now live in a full-width band under the library, so all of them fit at 1280x800
            // without an artificial window height.
            var (window, root) = OpenTemplatePanel(library, height: 800, show: false);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains(
                    texts,
                    line => line.Contains(artifact.HashSha256, StringComparison.Ordinal)
                        && line.Contains(ApprovalStageId, StringComparison.Ordinal)
                        && line.Contains(ArtifactKind, StringComparison.Ordinal)
                        && line.Contains("проверены", StringComparison.Ordinal));

                Assert.Contains(
                    texts,
                    line => line.Contains(approver, StringComparison.Ordinal)
                        && line.Contains("без подписи", StringComparison.Ordinal));

                Assert.Equal(
                    "The document satisfies the acceptance criteria.",
                    ((TextBox)FindControl(root, "LibraryUserApprovalCommentBox")).Text);
                Assert.True(((Button)FindControl(root, "LibraryApproveObservedArtifactButton")).IsEnabled);
                Assert.True(((Button)FindControl(root, "LibraryRejectObservedArtifactButton")).IsEnabled);

                var screenshotPath = CaptureScreenshot(root, "workflow_product_user_approval.png");

                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A refusal and a notice are durable records, so neither may quote the operator's own path, the content
    /// of their file, or a name the product is not allowed to invent.
    /// </summary>
    private static void AssertNoDetail(string surface, params string[] forbidden)
    {
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheShippedApprovalPanelBindsItsCommentItsTwoActionsAndTheIdentityLabel()
    {
        var library = new WorkflowLibraryViewModel();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenTemplatePanel(library);

            try
            {
                var commentBox = FindControl(root, "LibraryUserApprovalCommentBox");
                var approveButton = FindControl(root, "LibraryApproveObservedArtifactButton");
                var rejectButton = FindControl(root, "LibraryRejectObservedArtifactButton");

                // The comment is the only operator input here, and it is frozen by the same busy flag the
                // artifact path uses, because the command captures it before its first await.
                Assert.Equal("UserApprovalComment", BindingPathOf(commentBox, TextBox.TextProperty));
                Assert.Equal(
                    "IsUserApprovalInputEditable",
                    BindingPathOf(commentBox, UIElement.IsEnabledProperty));

                // The two actions are the view model's own commands and carry their own labels, so neither
                // of them can be mistaken for a default choice.
                Assert.Equal("ApproveObservedArtifactCommand", BindingPathOf(approveButton, ButtonBase.CommandProperty));
                Assert.Equal("RejectObservedArtifactCommand", BindingPathOf(rejectButton, ButtonBase.CommandProperty));
                Assert.Equal("Одобрить артефакт", ((Button)approveButton).Content);
                Assert.Equal("Отклонить артефакт", ((Button)rejectButton).Content);

                // A window without the composed services still ships the panel, unoffered and explaining why.
                Assert.False(((Button)approveButton).IsEnabled);
                Assert.False(((Button)rejectButton).IsEnabled);

                var texts = ReadTextBlocks(root);
                Assert.Contains(library.UserApprovalNotice, texts);
                Assert.Contains(library.UserApprovalApproverDisplay, texts);
                Assert.Contains("Локальный вход Windows", library.UserApprovalApproverDisplay, StringComparison.Ordinal);
                Assert.Contains("терминальным исходом «отклонено»", library.UserApprovalNotice, StringComparison.Ordinal);

                // No editable field anywhere in the panel is bound to a hash: the hash is read from the
                // run's stored artifact and cannot be typed or altered here.
                Assert.Empty(FindVisualDescendants<FrameworkElement>(root)
                    .OfType<TextBox>()
                    .Where(textBox => BindingPathOf(textBox, TextBox.TextProperty)
                        .Contains("Hash", StringComparison.Ordinal))
                    .Select(textBox => BindingPathOf(textBox, TextBox.TextProperty))
                    .ToArray());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Starts a run whose current pinned stage really is an approval gate, and records the artifact that
    /// stage requires - so the decision under test has verified committed content to be about.
    /// </summary>
    private static async Task<WorkflowLibraryViewModel> StartApprovableRunAsync(IServiceProvider services)
    {
        await AssignApprovalTemplateAsync(services, requiresUserApproval: true, requiresArtifact: true);

        var library = CreateLibrary(services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();

        Assert.Equal(ApprovalStageId, library.ObservedRun!.CurrentStageId);

        await AttachArtifactAsync(library, "the document under review");

        Assert.Single(library.ObservedRun!.Artifacts);

        return library;
    }

    private static async Task AttachArtifactAsync(WorkflowLibraryViewModel library, string content)
    {
        library.StageArtifactPath = WriteTempFile(content);
        await library.AttachStageArtifactAsync();

        Assert.Empty(library.Blocker);
    }

    /// <summary>
    /// A local path the operator's file would be read from. The approval path never sees it: the artifact is
    /// already stored, and the redaction assertions look for this path by accident in every message.
    /// </summary>
    private static string WriteTempFile(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "llmworkgui-phase10j-files");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.md");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

        return path;
    }

    private static async Task<WorkflowArtifactEvidence> RecordReplacementAsync(
        IWorkflowRunService runService,
        string runId,
        string content)
    {
        var recorded = await runService.RecordStageArtifactAsync(
            runId,
            ApprovalStageId,
            ArtifactKind,
            new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(recorded.Artifacts, runId, ApprovalStageId, ArtifactKind)!;
    }

    private static async Task AssignApprovalTemplateAsync(
        IServiceProvider services,
        bool requiresUserApproval,
        bool requiresArtifact)
    {
        var store = services.GetRequiredService<IWorkflowTemplateStore>();

        var gate = requiresUserApproval || requiresArtifact
            ? new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                Array.Empty<string>(),
                requiresUserApproval,
                requiresArtifact ? ArtifactKind : null)
            : null;

        await store.SaveAsync(new WorkflowTemplateDefinition(
            TemplateId,
            1,
            "Approval template",
            "A linear template whose entry stage is an approval gate.",
            new WorkflowGraph(ApprovalStageId, new[]
            {
                new WorkflowNodeDefinition(
                    ApprovalStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    gateMetadata: gate),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.Prompt,
                    "Node B",
                    "Role B",
                    successTargetNodeId: "node-c"),
                new WorkflowNodeDefinition("node-c", WorkflowNodeKind.TerminalOutcome, "Node C", "Role C")
            }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            SeededAt));

        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-approval",
            ProjectId,
            TemplateId,
            1,
            SeededAt));
    }

    private static WorkflowLibraryViewModel CreateLibrary(IServiceProvider services) =>
        services.GetRequiredService<WorkflowLibraryViewModel>();

    private static async Task<WorkflowRun> StoredRunAsync(IServiceProvider services, string runId) =>
        await services.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId)
        ?? throw new InvalidOperationException($"The run '{runId}' is expected to be stored.");

    /// <summary>
    /// Every user approval storage holds for the seeded project, read through the run repository rather than
    /// from a view model that was handed its own object - so a refused decision that somehow left a record
    /// would still show up here.
    /// </summary>
    private static async Task<IReadOnlyList<UserApprovalEvidence>> StoredApprovalsAsync(IServiceProvider services)
    {
        var runs = await services
            .GetRequiredService<IWorkflowRunRepository>()
            .GetByProjectIdAsync(ProjectId);

        return runs.SelectMany(run => run.Approvals).ToArray();
    }

    private void DeleteCommittedBlob(string blobId)
    {
        var hex = blobId["sha256:".Length..];
        var path = Path.Combine(_appData, "blobs", "sha256", hex[..2], hex);

        Assert.True(File.Exists(path), "The committed artifact blob is expected to exist before it is deleted.");
        File.Delete(path);
    }

    private async Task SeedAsync(IServiceProvider services)
    {
        var blobStore = services.GetRequiredService<WorkflowBlobStore>();
        await using (var content = new MemoryStream(CreateImportedWorkflowZip()))
        {
            await blobStore.SaveBlobAsync(content);
        }

        var blobId = WorkflowBlobStore.ComputeBlobId(CreateImportedWorkflowZip());

        await using var connection = await _factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($project, 'Approval project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'ZipArchive', $blob, $blob, $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version1, $package, 1, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version2, $package, 2, $blob, $blob, 'ZipArchive', $now);
            INSERT INTO WorkflowBindings (Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('9d2f7c14-5b3a-4c8e-8f21-6a0d4e2b7c55', $project, $package, $version2, NULL, $now, $now);
            """;

        command.Parameters.AddWithValue("$project", ProjectId);
        command.Parameters.AddWithValue("$package", PackageId);
        command.Parameters.AddWithValue("$version1", VersionOneId);
        command.Parameters.AddWithValue("$version2", VersionTwoId);
        command.Parameters.AddWithValue("$blob", blobId);
        command.Parameters.AddWithValue("$now", "2026-09-29T00:00:00Z");

        await command.ExecuteNonQueryAsync();
    }

    private static byte[] CreateImportedWorkflowZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("README.md").Open());
            writer.Write("Imported workflow package.");
        }

        return buffer.ToArray();
    }

    private IHost CreateHost(RunServiceGate? gate = null) =>
        HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();

                if (gate is not null)
                {
                    SuspendRunServiceAt(services, gate);
                }
            })
            .Build();

    private static void SuspendRunServiceAt(IServiceCollection services, RunServiceGate gate)
    {
        var product = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IWorkflowRunService))
            ?? throw new InvalidOperationException("The product composition no longer registers IWorkflowRunService.");

        var productFactory = product.ImplementationFactory
            ?? throw new InvalidOperationException(
                "IWorkflowRunService is expected to be registered through its own factory.");

        services.Remove(product);
        services.AddSingleton<IWorkflowRunService>(provider => new GatedWorkflowRunService(
            (IWorkflowRunService)productFactory(provider),
            gate));
    }

    private async Task<IHost> CreateInitializedHostAsync(RunServiceGate? gate = null)
    {
        var host = CreateHost(gate);
        await HostBootstrapper.InitializeAsync(host);

        return host;
    }

    private static (Window Window, FrameworkElement Root) OpenTemplatePanel(
        WorkflowLibraryViewModel viewModel,
        double height = 900,
        bool show = true)
    {
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 1280,
            Height = height,
            Content = host,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml",
                UriKind.Absolute)
        });

        // A shown window is clamped to the desktop work area, which is what makes the shipped layout
        // observable; an offscreen one is not, so a capture can cover the whole column at once. Both are
        // measured and arranged at the requested size, and both resolve the same shipped styles.
        if (show)
        {
            window.Show();
        }

        host.Measure(new Size(1280, height));
        host.Arrange(new Rect(0, 0, 1280, height));
        host.UpdateLayout();

        return (window, host);
    }

    private static FrameworkElement FindControl(DependencyObject root, string name) =>
        FindVisualDescendants<FrameworkElement>(root).Single(element => element.Name == name);

    private static string BindingPathOf(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBindingExpression(element, property)?.ParentBinding.Path.Path ?? string.Empty;

    /// <summary>
    /// Renders the real loaded panel to a PNG under the suite's screenshot directory, so the shipped layout
    /// of this slice is evidence rather than a claim.
    /// </summary>
    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(1000, root.ActualWidth);
        var height = (int)Math.Max(640, root.ActualHeight);

        var renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        var screenshotPath = Path.Combine(ScreenshotFile.OutputDirectory, fileName);
        ScreenshotFile.Save(encoder, screenshotPath);

        Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");

        return screenshotPath;
    }

    private static string[] ReadTextBlocks(DependencyObject root) =>
        FindVisualDescendants<TextBlock>(root)
            .Select(textBlock => textBlock.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Runs the dispatcher's idle work, so the binding updates the command produced are delivered before the
    /// assertions are read.
    /// </summary>
    private static void DrainDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.SystemIdle,
            new System.Windows.Threading.DispatcherOperationCallback(
                state => ((System.Windows.Threading.DispatcherFrame)state!).Continue = false),
            frame);

        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// A one-shot suspension point inside the run service's user approval, putting a test exactly where the
    /// screen cannot see the outcome yet: the decision has been captured, and nothing has been stored.
    /// </summary>
    private sealed class RunServiceGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Open() => _release.TrySetResult();

        public async Task WaitAsync()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The product run service with only its user approval suspended at the gate. Every other command, and
    /// the whole run each one produces, is the real one.
    /// </summary>
    private sealed class GatedWorkflowRunService : IWorkflowRunService
    {
        private readonly IWorkflowRunService _inner;
        private readonly RunServiceGate _gate;

        public GatedWorkflowRunService(IWorkflowRunService inner, RunServiceGate gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public async Task<WorkflowRun> RecordUserApprovalAsync(
            string runId,
            UserApprovalEvidence approval,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            return await _inner
                .RecordUserApprovalAsync(runId, approval, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<WorkflowRun> StartRunAsync(
            string projectId,
            string workflowPackageId,
            string workflowVersionId,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            _inner.StartRunAsync(projectId, workflowPackageId, workflowVersionId, sessionId, cancellationToken);

        public Task<WorkflowRun> StartLegacyRunAsync(
            string projectId,
            string workflowPackageId,
            string workflowVersionId,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            _inner.StartLegacyRunAsync(projectId, workflowPackageId, workflowVersionId, sessionId, cancellationToken);

        public Task<WorkflowRun> RecordReviewerVerdictAsync(
            string runId,
            ReviewerVerdictRecord verdict,
            CancellationToken cancellationToken = default) =>
            _inner.RecordReviewerVerdictAsync(runId, verdict, cancellationToken);

        public Task<WorkflowRun> RecordLegacyUnlinkedReviewerVerdictAsync(
            string runId,
            ReviewerVerdictRecord verdict,
            CancellationToken cancellationToken = default) =>
            _inner.RecordLegacyUnlinkedReviewerVerdictAsync(runId, verdict, cancellationToken);

        public Task<WorkflowRun> AdvanceStageAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.AdvanceStageAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> AdvanceToDeclaredFailureStageAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.AdvanceToDeclaredFailureStageAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> RecordStageArtifactAsync(
            string runId,
            string stageId,
            string kind,
            Stream content,
            DataClassification classification,
            CancellationToken cancellationToken = default) =>
            _inner.RecordStageArtifactAsync(runId, stageId, kind, content, classification, cancellationToken);

        public Task<WorkflowRun> CancelRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.CancelRunAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> FailRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.FailRunAsync(runId, reason, cancellationToken);

        public Task<WorkflowRun> CompleteRunAsync(
            string runId,
            string reason,
            CancellationToken cancellationToken = default) =>
            _inner.CompleteRunAsync(runId, reason, cancellationToken);
    }
}

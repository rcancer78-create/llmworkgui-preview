using System;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class WorkflowRunProductCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-owned")]
    [InlineData("collection-source")]
    public async Task CollectionFileActionUsesVerifiedExecutionAndExistingArtifactControls(string executionId)
    {
        using var host = await CreateInitializedHostAsync();
        var store = host.Services.GetRequiredService<IWorkflowTemplateStore>();
        await store.SaveAsync(new WorkflowTemplateDefinition("collection-ui", 1, "Collection", "Collection",
            new WorkflowGraph("collect", [
                new WorkflowNodeDefinition("collect", WorkflowNodeKind.ArtifactCollection, "Collect", "Collector",
                    successTargetNodeId: "done", artifactContract: ArtifactKind,
                    gateMetadata: new WorkflowNodeGateMetadata(WorkflowStageKind.Custom, [], false, ArtifactKind)),
                new WorkflowNodeDefinition("done", WorkflowNodeKind.TerminalOutcome, "Done", "Collector")]),
            [], [], false, DateTimeOffset.UtcNow));
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment("collection-ui-assignment", ProjectId, "collection-ui", 1, DateTimeOffset.UtcNow));
        var library = CreateLibrary(host.Services);
        await library.RefreshAsync();
        await library.StartAssignedRunAsync();
        Assert.True(library.StageArtifactRequiresExecution);
        var run = library.ObservedRun!;
        await using (var connection = await _factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                    State,ReconciliationOutcome,CloseReason,WorkflowRunId,CreatedAtUtc,LastEventAtUtc)
                SELECT 'collection-session',$project,Backend,ProviderProfileId,AccountId,ModelId,$root,
                    'Idle','None','None',$run,$stamp,$stamp FROM Routes WHERE Id=$route;
                INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,CreatedAtUtc,StartedAtUtc,EndedAtUtc)
                VALUES ('collection-source','collection-session','collection-request','Succeeded','None',$route,$stamp,$stamp,$stamp);
                """;
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$root", _root);
            command.Parameters.AddWithValue("$run", run.Id);
            command.Parameters.AddWithValue("$route", ReviewerRouteId);
            command.Parameters.AddWithValue("$stamp", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        library.StageArtifactExecutionId = executionId;
        library.StageArtifactPath = WriteArtifactFile("collected.md", Encoding.UTF8.GetBytes("collected bytes"));
        if (executionId == "collection-source")
        {
            StaTestRunner.Run(() =>
            {
                var opened = OpenTemplatePanel(library);
                try
                {
                    var input = FindVisualDescendants<TextBox>(opened.Root).Single(x => x.Name == "LibraryStageArtifactExecutionBox");
                    Assert.True(input.IsVisible);
                    Assert.Equal("StageArtifactExecutionId", BindingPathOf(input, TextBox.TextProperty));
                    Assert.Equal(executionId, input.Text);
                }
                finally { opened.Window.Close(); }
            });
        }
        await library.AttachStageArtifactAsync();
        if (executionId == "collection-source")
        {
            Assert.Equal(executionId, Assert.Single(library.ObservedRun!.Artifacts).ExecutionId);
            Assert.Contains("Связано с выполнением", library.RunCommandNotice);
        }
        else
        {
            Assert.Empty(library.ObservedRun!.Artifacts);
            Assert.False(string.IsNullOrWhiteSpace(library.Blocker));
        }
    }
}

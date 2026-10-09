using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Legacy;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class SupervisedLegacyRunnerIntegrationTests
{
    [Theory]
    [InlineData("CODEX_HOME")]
    [InlineData("codex_home")]
    public async Task CallerCannotInjectNativeAccountContextIntoDeclaredLegacyChild(string variableName)
    {
        await InitializeDatabaseAsync();
        var blob = await StoreArchiveAsync(archive => WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1",
            "Write-Output ('native-context:' + $env:CODEX_HOME); Write-Output ('ordinary-setting:' + $env:LEGACY_NOTE); exit 0"));
        var injectedContext = Path.Combine(_appData.Root, "caller-native-account-context");
        var request = new LegacyWorkflowExecutionRequest("project-1", _checkoutPath, "package-1", "version-1",
            blob.BlobId, new WorkflowEntrypointDescriptor("run.ps1", WorkflowEntrypointKind.PowerShell, true),
            environmentVariables: new Dictionary<string, string>
            {
                [variableName] = injectedContext,
                ["LEGACY_NOTE"] = "safe-caller-setting"
            }, executionId: LegacyExecutionId);
        var result = await _runner.ExecuteAsync(request);
        var output = await File.ReadAllTextAsync(result.StandardOutputLogPath);
        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(injectedContext, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ordinary-setting:safe-caller-setting", output, StringComparison.Ordinal);
        await AssertRunIsFullyReleasedAsync(result.ExecutionId, blob.BlobId);
        // Actual supervised local script/environment evidence; no native provider/model I/O or network sandbox claim.
    }
}

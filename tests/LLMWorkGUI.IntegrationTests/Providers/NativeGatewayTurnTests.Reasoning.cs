using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    private async Task EnableReasoning(string value = "high", bool expired = false)
    {
        var snapshot = await new SqliteModelRouteConfigurationService(_db.Factory, _guard!,
            new SensitiveDataFilter(), TimeProvider.System).ReadAsync();
        var model = Assert.Single(snapshot.Models.Where(m => m.Backend == BackendType.NativeGateway));
        var account = Assert.Single(snapshot.Accounts.Where(a => a.ProfileId == model.ProfileId));
        var store = new SqliteModelCapabilityEvidenceStore(_db.Factory, _guard!, new SensitiveDataFilter(), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(model.Id, account.Id, CapabilityState.Supported, ModelProvenance.PluginReported,
            now.AddMinutes(-2), expired ? now.AddMinutes(-1) : now.AddMinutes(5),
            ModelCapabilityFlags.ReasoningVariants, [value, "max"], [], []) { DiscoverySource = "Scoped test fixture" },
            await store.CaptureContextAsync(model.Id, account.Id));
        await Sql("UPDATE Routes SET ReasoningEffort='" + value + "' WHERE Backend='NativeGateway'");
    }

    [Fact]
    public async Task ReasoningRoutePassesExactOptionToOwnedCodexProcessAndPersistsAdmission()
    {
        await Ready(); await EnableReasoning();
        var selected = Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        Assert.Equal("high", selected.Binding.ReasoningEffort);
        var paths = CreateDispatchProcess();
        var script = Path.Combine(_db.GetWorkspacePath(), "node_modules", "dispatch-fixture", "cli.mjs");
        await File.AppendAllTextAsync(script, "\nfs.writeFileSync(path.join(root, 'argv.json'), JSON.stringify(process.argv.slice(2)));\n");
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<CodexAdapter>.Instance), paths.Shim);
        var result = await _service!.ExecuteAsync(_request with { ExpectedBinding = selected.Binding });
        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal("high", await Sql("SELECT ReasoningEffort FROM Sessions WHERE Backend='NativeGateway'"));
        Assert.Equal("high", await Sql("SELECT json_extract(NormalizedRedactedPayloadJson,'$.reasoningEffort') FROM ExecutionEvents WHERE json_extract(NormalizedRedactedPayloadJson,'$.state')='Starting'"));
        var argv = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(_db.GetWorkspacePath(), "argv.json")))!;
        var index = Array.IndexOf(argv, "model_reasoning_effort=high");
        Assert.True(index > 0); Assert.Equal("-c", argv[index - 1]);
        Assert.True(File.Exists(paths.Sent));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("other-account")]
    [InlineData("unsupported")]
    [InlineData("unsafe-option")]
    public async Task ReasoningAdmissionRejectsMissingStaleUnscopedOrUnsupportedEvidence(string scenario)
    {
        await Ready(); await EnableReasoning(expired: scenario == "expired");
        if (scenario == "missing") await Sql("DELETE FROM ModelCapabilities");
        if (scenario == "other-account") await Sql("UPDATE ModelCapabilities SET CapabilityValue=json_set(CapabilityValue,'$.AccountId','another-account')");
        if (scenario == "unsupported") await Sql("UPDATE Routes SET ReasoningEffort='low' WHERE Backend='NativeGateway'");
        if (scenario == "unsafe-option") await Sql("UPDATE Routes SET ReasoningEffort='HIGH' WHERE Backend='NativeGateway'");
        Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0, _factoryCalls); Assert.Equal(0, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task ReasoningSelectionIsPinnedAcrossRouteChangesBeforeAdmission()
    {
        await Ready(); await EnableReasoning();
        var selected = Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        await Sql("UPDATE Routes SET ReasoningEffort='max' WHERE Backend='NativeGateway'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request with { ExpectedBinding = selected.Binding }));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData("DELETE FROM ModelCapabilities")]
    [InlineData("UPDATE Routes SET ReasoningEffort='max' WHERE Backend='NativeGateway'")]
    [InlineData("UPDATE Sessions SET ReasoningEffort='max' WHERE Backend='NativeGateway'")]
    public async Task ReasoningRevocationAtNativeBoundaryNeverResumesChildOrSendsPrompt(string mutation)
    {
        await Ready(); await EnableReasoning();
        var paths = CreateDispatchProcess(); var adapter = new DispatchProcessAdapter();
        UseDispatchGateway(adapter, paths.Shim);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = _service!.ExecuteAsync(_request, deadline.Token);
        try
        {
            await adapter.BeforeProcess.Task.WaitAsync(deadline.Token);
            await Sql(mutation); adapter.ReleaseProcess.TrySetResult();
            var result = await pending;
            Assert.Equal(ExecutionState.Failed, result.State); Assert.False(result.RequiresReconciliation);
            Assert.False(File.Exists(paths.Sent));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='NativeProcessBound'"));
        }
        finally { adapter.ReleaseProcess.TrySetResult(); deadline.Cancel(); }
    }

    [Fact]
    public async Task ReasoningCannotBeForgedByRewritingRouteSessionAndEntryAfterAdmission()
    {
        await Ready(); await EnableReasoning();
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard!, TimeProvider.System);
        var entry = await journal.BeginAsync(_request, CancellationToken.None);
        await using var checkout = await _locks.AcquireWriterLockAsync(_request.ProjectId, _request.RootPath, entry.ExecutionId, 0);
        await Sql("UPDATE Routes SET ReasoningEffort='max' WHERE Backend='NativeGateway'; UPDATE Sessions SET ReasoningEffort='max' WHERE Backend='NativeGateway';");
        var forged = entry with { Context = entry.Context with { ReasoningEffort = "max" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BindProcessAsync(forged, 123, CancellationToken.None));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ProcessGeneration!=0"));
        Assert.Equal("Starting", await Sql("SELECT State FROM Executions"));
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Theory]
    [InlineData("UPDATE ProjectLocks SET ProcessGeneration=999")]
    [InlineData("UPDATE ExecutionEvents SET NormalizedRedactedPayloadJson='{}' WHERE EventKind='NativeProcessBound'")]
    [InlineData("DELETE FROM ExecutionEvents WHERE EventKind='NativeProcessBound'")]
    public async Task BoundProcessDispatchRejectsChangedGenerationOrMissingDurableProof(string mutation)
    {
        await Ready();
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard!, TimeProvider.System);
        var entry = await journal.BeginAsync(_request, CancellationToken.None);
        await using var checkout = await _locks.AcquireWriterLockAsync(_request.ProjectId, _request.RootPath,
            entry.ExecutionId, 0);
        await journal.BindProcessAsync(entry, 123, CancellationToken.None);
        await Sql(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry,
            CancellationToken.None, processGeneration: 123));
        Assert.Equal("Starting", await Sql("SELECT State FROM Executions"));
        Assert.True(checkout.IsHeld);
    }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    public async Task QueuedAccountSlot_RevalidatesPolicyBeforeNativePromptDispatch(string mutation)
    {
        await Ready();
        var paths = CreateDispatchProcess();
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<CodexAdapter>.Instance), paths.Shim);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var occupying = _gateway!.CompleteAsync(new ChatRequest
        {
            Model = "codex/work/model", AccountId = "work", Messages = [ChatMessage.User("HOLD_SLOT")]
        }, deadline.Token);
        try
        {
            await WaitForDispatchFile(paths.Holding, deadline.Token);
            var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _store.OnRead = () => routed.TrySetResult();
            var pending = _service!.ExecuteAsync(_request, deadline.Token);
            await routed.Task.WaitAsync(deadline.Token);
            Assert.False(pending.IsCompleted);
            Assert.False(File.Exists(paths.Sent));
            await Sql(mutation);
            await File.WriteAllTextAsync(paths.Release, "release", deadline.Token);
            await occupying;
            var result = await pending;
            Assert.Equal(ExecutionState.Failed, result.State);
            Assert.False(result.RequiresReconciliation);
            Assert.False(File.Exists(paths.Sent));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE json_extract(NormalizedRedactedPayloadJson,'$.state')='Running'"));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
        }
        finally
        {
            _store.OnRead = null;
            await File.WriteAllTextAsync(paths.Release, "release");
            deadline.Cancel();
            try { await occupying; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    public async Task NativeBoundary_RevalidatesAfterAdapterPreparationBeforeProcessStart(string mutation)
    {
        await Ready();
        var paths = CreateDispatchProcess();
        var adapter = new DispatchProcessAdapter();
        UseDispatchGateway(adapter, paths.Shim);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = _service!.ExecuteAsync(_request, deadline.Token);
        try
        {
            await adapter.BeforeProcess.Task.WaitAsync(deadline.Token);
            Assert.False(File.Exists(paths.Sent));
            await Sql(mutation);
            adapter.ReleaseProcess.TrySetResult();
            var result = await pending;
            Assert.Equal(ExecutionState.Failed, result.State);
            Assert.False(result.RequiresReconciliation);
            Assert.False(File.Exists(paths.Sent));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
        }
        finally { adapter.ReleaseProcess.TrySetResult(); deadline.Cancel(); }
    }

    [Fact]
    public async Task DispatchProof_RefusesAdapterWithoutTransportBoundaryBeforeInvocation()
    {
        await Ready();
        _gateway!.Dispose();
        _gateway = new LlmGateway(_store, [new UntrustedDispatchAdapter(_adapter)], new GatewayOptions());
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.Equal(0, _adapter.Calls);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
    }

    [Fact]
    public async Task DispatchBoundary_CurrentPolicyAllowsOwnedNativeProcessAndTerminalRelease()
    {
        await Ready();
        var paths = CreateDispatchProcess();
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<CodexAdapter>.Instance), paths.Shim);
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.True(File.Exists(paths.Sent));
        Assert.Contains("private prompt fixture", await File.ReadAllTextAsync(paths.Sent));
        Assert.Equal(1L, await Sql("""
            SELECT COUNT(*) FROM ExecutionEvents e JOIN ProjectLocks l ON l.ExecutionId=e.ExecutionId
            WHERE e.EventKind='NativeProcessBound' AND l.ProcessGeneration>0
                AND json_extract(e.NormalizedRedactedPayloadJson,'$.processGeneration')=l.ProcessGeneration
                AND json_extract(e.NormalizedRedactedPayloadJson,'$.applicationInstanceId')=l.ApplicationInstanceId
            """));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
    }

    [Fact]
    public async Task NativeProcessBindingCommitFailureNeverResumesChildOrSendsPrompt()
    {
        await Ready();
        var paths = CreateDispatchProcess();
        UseDispatchGateway(new CodexAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<CodexAdapter>.Instance), paths.Shim);
        await Sql("""
            CREATE TRIGGER reject_native_binding BEFORE INSERT ON ExecutionEvents
            WHEN NEW.EventKind='NativeProcessBound' BEGIN SELECT RAISE(ABORT,'fixture binding refused'); END;
            """);
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.False(result.RequiresReconciliation);
        Assert.False(File.Exists(paths.Sent));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ProcessGeneration!=0"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='NativeProcessBound'"));
    }

    private (string Shim, string Holding, string Release, string Sent) CreateDispatchProcess()
    {
        var root = _db.GetWorkspacePath();
        var directory = Path.Combine(root, "node_modules", "dispatch-fixture");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cli.mjs"), """
            import fs from 'node:fs';
            import path from 'node:path';
            import { fileURLToPath } from 'node:url';
            const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
            let prompt = '';
            for await (const chunk of process.stdin) prompt += chunk;
            if (prompt.includes('HOLD_SLOT')) {
              fs.writeFileSync(path.join(root, 'holding'), 'owned fixture');
              const until = Date.now() + 15000;
              while (!fs.existsSync(path.join(root, 'release'))) {
                if (Date.now() >= until) process.exit(8);
                await new Promise(resolve => setTimeout(resolve, 20));
              }
            } else fs.writeFileSync(path.join(root, 'sent'), prompt);
            console.log(JSON.stringify({type:'item.completed', item:{type:'agent_message', text:'answer'}}));
            console.log(JSON.stringify({type:'turn.completed', usage:{input_tokens:1, output_tokens:1}}));
            """);
        var shim = Path.Combine(root, "dispatch.cmd");
        File.WriteAllText(shim, "@node \"%dp0%\\node_modules\\dispatch-fixture\\cli.mjs\" %*\n");
        return (shim, Path.Combine(root, "holding"), Path.Combine(root, "release"), Path.Combine(root, "sent"));
    }

    private void UseDispatchGateway(IProviderAdapter adapter, string shim)
    {
        _gateway!.Dispose();
        _store.Account.Executable = shim;
        _store.Account.WorkingDirectory = _db.GetWorkspacePath();
        _gateway = new LlmGateway(_store, [adapter], new GatewayOptions
        {
            WorkspaceDirectory = _db.GetWorkspacePath(), MaxConcurrentRequestsPerAccount = 1
        }, _gatewayLog);
    }

    private static async Task WaitForDispatchFile(string path, CancellationToken token)
    {
        while (!File.Exists(path)) await Task.Delay(20, token);
    }

    private sealed class DispatchProcessAdapter : NativeAdapterBase
    {
        internal override bool SupportsNativeDispatchAuthorization => true;
        public DispatchProcessAdapter() : base(new ExecutableResolver(), new GatewayOptions(), NullLogger.Instance) { }
        public TaskCompletionSource BeforeProcess { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProcess { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ProviderKind Provider => ProviderKind.Codex;
        public override string DisplayName => "Owned dispatch fixture";
        public override string DefaultExecutable => "fixture";
        public override ProviderCapabilities Capabilities { get; } = new(false, "fixture", MultiAccountSupport.Isolated, "fixture", null, null, true, 1_000_000);
        protected override IReadOnlyList<string>? LoginArguments(AccountProfile account) => null;
        public override Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public override async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            BeforeProcess.TrySetResult();
            await ReleaseProcess.Task.WaitAsync(token);
            await foreach (var item in StreamAsync(account, request, Launch(account, [], request.WorkingDirectory, request.Prompt), new DispatchParser(), token))
                yield return item;
        }
        private sealed class DispatchParser : IChatLineParser
        {
            public IEnumerable<NativeChatEvent> Parse(JsonElement line) =>
                line.GetProperty("type").GetString() == "item.completed"
                    ? [NativeChatEvent.Delta(line.GetProperty("item").GetProperty("text").GetString()!)]
                    : [];
        }
    }

    private sealed class UntrustedDispatchAdapter(IProviderAdapter inner) : IProviderAdapter
    {
        public ProviderKind Provider => inner.Provider;
        public string DisplayName => inner.DisplayName;
        public string DefaultExecutable => inner.DefaultExecutable;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public string? ResolveExecutable(AccountProfile account) => inner.ResolveExecutable(account);
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => inner.GetStatusAsync(account, token);
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) => inner.ListModelsAsync(account, token);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) => inner.GetQuotaAsync(account, token);
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => inner.StartInteractiveLoginAsync(account, token);
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token) => inner.RunChatAsync(account, request, token);
    }
}

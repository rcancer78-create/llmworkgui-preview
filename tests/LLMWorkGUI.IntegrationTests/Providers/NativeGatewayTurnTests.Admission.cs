using LLMGateway.Core;
using LLMGateway.Core.Client;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("provider")]
    [InlineData("arguments")]
    [InlineData("executable")]
    [InlineData("preparation")]
    public async Task LocalGatewayPreAdmissionFailure_ClosesWithoutHealthDamageOrRetainedOwnership(string scenario)
    {
        await Ready();
        if (scenario == "disabled") _store.Account.Enabled = false;
        if (scenario == "provider") _store.Account.Provider = ProviderKind.Grok;
        if (scenario == "arguments") _store.Account.ExtraArguments = ["--model", "other"];
        if (scenario == "executable") _adapter.ExecutableMissing = true;
        if (scenario == "preparation") _adapter.RefusePreparation = true;

        var result = await _service!.ExecuteAsync(_request);

        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(0, _adapter.Calls);
        Assert.False(Assert.Single(_tokens).IsHeld);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
        Assert.NotEqual(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal("Closed", await Sql("SELECT State FROM Sessions WHERE Backend='NativeGateway'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalGatewayAccountSlotWait_CancelsBeforeDispatchWithoutHealthDamage(bool timeout)
    {
        await Ready(); _adapter.Wait = true;
        var occupant = _gateway!.CompleteAsync(CoreRequest());
        await _adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.OnRead = () => admission.TrySetResult();
        using var caller = new CancellationTokenSource();
        try
        {
            var pending = _service!.ExecuteAsync(_request with { Timeout = TimeSpan.FromSeconds(timeout ? 1 : 10) }, caller.Token);
            await admission.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!timeout) caller.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(timeout ? ExecutionState.TimedOut : ExecutionState.Cancelled, result.State);
            Assert.False(result.RequiresReconciliation);
            Assert.Equal(1, _adapter.Calls); // Only the independently admitted occupant ran.
            Assert.False(Assert.Single(_tokens).IsHeld);
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
            Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        }
        finally
        {
            _adapter.Release.TrySetResult();
            await occupant.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RemoteHttpGatewayBindingRefusalBeforeSend_ClosesWithoutRetainedOwnership()
    {
        await Ready();
        using var handler = new LostResponseHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.invalid/") };
        using var remote = new OpenAiGatewayClient(http);
        var service = new NativeGatewayTurnService(() => remote, _db.Factory, _guard!, _locks, _health,
            new SensitiveDataFilter(), TimeProvider.System);
        var result = await service.ExecuteAsync(_request);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.False(result.RequiresReconciliation);
        Assert.False(Assert.Single(_tokens).IsHeld);
        Assert.NotEqual(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
    }

    [Fact]
    public async Task OpaqueGatewayFailureBeforeFirstReceivedEvent_StillRetainsUncertainOwnership()
    {
        await Ready();
        var opaque = new DispatchLossGateway();
        var service = new NativeGatewayTurnService(() => opaque, _db.Factory, _guard!, _locks, _health,
            new SensitiveDataFilter(), TimeProvider.System);
        var result = await service.ExecuteAsync(_request);
        Assert.Equal(1, opaque.Calls);
        Assert.Equal(ExecutionState.Ambiguous, result.State);
        Assert.True(result.RequiresReconciliation);
        Assert.True(Assert.Single(_tokens).IsHeld);
        Assert.Equal(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
    }

    // This fixture models an unknown implementation after transport invocation,
    // not the known HTTP client's deterministic unsupported-binding refusal.
    private sealed class DispatchLossGateway : ILlmGateway
    {
        public int Calls;
        public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.FromException(new HttpRequestException("synthetic lost response after transport invocation"));
            yield break;
        }
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LostResponseHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("synthetic lost response after request dispatch");
        }
    }
}

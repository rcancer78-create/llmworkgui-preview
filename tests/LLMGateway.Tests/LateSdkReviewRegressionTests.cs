using System.Runtime.CompilerServices;
using LLMGateway.Core;

namespace LLMGateway.Tests;

public sealed class LateSdkReviewRegressionTests
{
    [Theory]
    [InlineData(AccountAvailability.AuthenticationRequired)]
    [InlineData(AccountAvailability.Error)]
    [InlineData(AccountAvailability.Ready)]
    public async Task ReadyQuotaMustNotPublishAnEarlierFailureMessage(AccountAvailability initial)
    {
        var adapter = new Adapter(initial);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Profile()]), [adapter], new GatewayOptions());
        await gateway.CheckAccountAsync("owned");
        var snapshot = Assert.Single(await gateway.GetQuotasAsync(true, "owned"));
        Assert.Equal(AccountAvailability.Ready, snapshot.Availability);
        var account = Assert.Single(await gateway.GetAccountsAsync());
        Assert.Equal(AccountAvailability.Ready, account.Availability);
        Assert.Null(account.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalAfterACommittedMutationMustNotThrowNullReference(bool update)
    {
        var store = new HeldReturnStore(update);
        using var gateway = new LlmGateway(store, [new Adapter(AccountAvailability.Ready)], new GatewayOptions());
        var operation = update ? gateway.UpdateAccountAsync(Profile()) : gateway.AddAccountAsync(Profile());
        await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gateway.RemoveAccountAsync("owned");
        store.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<GatewayException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(GatewayErrorKind.NotFound, error.Kind);
    }

    private static AccountProfile Profile() => new() { Id = "owned", Provider = ProviderKind.Codex, IsActive = true };

    [Theory]
    [InlineData(AccountAvailability.AuthenticationRequired)]
    [InlineData(AccountAvailability.Error)]
    public async Task UnknownQuotaCannotClearAnAuthoritativeFailure(AccountAvailability initial)
    {
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Profile()]),
            [new Adapter(initial, AccountAvailability.Unknown, "unsupported quota")], new GatewayOptions());
        await gateway.CheckAccountAsync("owned");
        await gateway.GetQuotasAsync(true, "owned");
        var account = Assert.Single(await gateway.GetAccountsAsync());
        Assert.Equal(initial, account.Availability);
        Assert.Equal("old synthetic failure", account.Message);
    }

    [Fact]
    public async Task KnownQuotaFailurePublishesItsOwnDiagnostic()
    {
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([Profile()]),
            [new Adapter(AccountAvailability.Ready, AccountAvailability.Error, "fresh quota failure")], new GatewayOptions());
        await gateway.CheckAccountAsync("owned");
        await gateway.GetQuotasAsync(true, "owned");
        var account = Assert.Single(await gateway.GetAccountsAsync());
        Assert.Equal(AccountAvailability.Error, account.Availability);
        Assert.Equal("fresh quota failure", account.Message);
    }

    private sealed class HeldReturnStore(bool update) : IAccountStore
    {
        private readonly JsonAccountStore _inner = JsonAccountStore.InMemory(update ? [Profile()] : []);
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AccountProfile> GetAll() => _inner.GetAll();
        public AccountProfile? Find(string id) => _inner.Find(id);
        public async Task AddAsync(AccountProfile profile, CancellationToken token = default)
        {
            await _inner.AddAsync(profile, token);
            Committed.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        public async Task UpdateAsync(AccountProfile profile, CancellationToken token = default)
        {
            await _inner.UpdateAsync(profile, token);
            Committed.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        public Task RemoveAsync(string id, CancellationToken token = default) => _inner.RemoveAsync(id, token);
        public Task SelectAsync(string id, CancellationToken token = default) => _inner.SelectAsync(id, token);
    }

    private sealed class Adapter(AccountAvailability initial, AccountAvailability quota = AccountAvailability.Ready, string? quotaMessage = null) : IProviderAdapter
    {
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Owned fixture";
        public string DefaultExecutable => "owned-fake";
        public ProviderCapabilities Capabilities { get; } = new(true, "owned", MultiAccountSupport.Isolated, "owned", null, null, true, 100000);
        public string? ResolveExecutable(AccountProfile profile) => "owned-fake";
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public Task<AccountStatus> GetStatusAsync(AccountProfile profile, CancellationToken token) => Task.FromResult(new AccountStatus(initial, null, "owned", initial == AccountAvailability.Ready ? null : "old synthetic failure", DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile profile, CancellationToken token) => Task.FromResult<IReadOnlyList<NativeModel>>([]);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile profile, CancellationToken token) => Task.FromResult(new QuotaSnapshot(profile.Id, profile.Provider, DateTimeOffset.UtcNow, quota, true, null, [], "owned", quotaMessage));
        public Task StartInteractiveLoginAsync(AccountProfile profile, CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile profile, NativeChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

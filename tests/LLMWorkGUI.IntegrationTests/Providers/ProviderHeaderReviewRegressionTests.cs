using System.Net;
using System.Runtime.Versioning;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class ProviderHeaderReviewRegressionTests
{
    private static ProviderProfile Profile(string id = "provider", string name = "Provider", IReadOnlyList<ProviderHeader>? headers = null) =>
        new(id, name, BackendType.OpenCode, "https://provider.test", null, DataClassification.PrivateSource, true, customHeaders: headers);

    [Fact]
    public async Task TwoLifecycleInstances_CannotRollbackAnotherCommittedApiKeyRotation()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var initial = new SecretLifecycleService(payloads, metadata, repo);
        var original = await initial.SaveProviderConfigurationAsync(Profile(), [], "original-key");
        var store = new BlockingPayloadStore(payloads);
        using var one = new SecretLifecycleService(store, metadata, repo);
        using var two = new SecretLifecycleService(store, metadata, repo);
        var first = one.SaveProviderConfigurationAsync(original.Profile, [], "committed-first-key");
        Task<ProviderConfigurationSaveResult>? second = null;
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = two.SaveProviderConfigurationAsync(original.Profile, [], "stale-second-key");
            Assert.False(second.IsCompleted);
            Assert.Equal(1, store.SaveCount);
        }
        finally { store.Release.TrySetResult(); }
        var committed = await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second!);
        Assert.NotEqual(original.ApiKeySecretReference, committed.ApiKeySecretReference);
        Assert.Null(await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        Assert.Equal("committed-first-key", await payloads.GetSecretAsync(committed.ApiKeySecretReference!));
        Assert.Equal(committed.ApiKeySecretReference, await repo.GetApiKeySecretReferenceAsync("provider"));
    }

    [Fact]
    public async Task ConcurrentRepositoryWriter_IsNotOverwritten_AndNewHeaderIsCompensated()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var store = new InterceptingStore(payloads);
        using var lifecycle = new SecretLifecycleService(store, metadata, repo);
        var original = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "old-value")]);
        store.BeforeSave = async () => await repo.UpsertAsync(Profile(name: "Concurrent edit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SaveProviderConfigurationAsync(original.Profile, [new("X-Token", "new-value")]));
        var persisted = (await repo.GetByIdAsync("provider"))!;
        Assert.Equal("Concurrent edit", persisted.DisplayName);
        var old = original.Profile.CustomHeaders![0].SecretReference!;
        Assert.Equal(old, Assert.Single(persisted.CustomHeaders!).SecretReference);
        Assert.Equal("old-value", await payloads.GetSecretAsync(old));
        var discarded = Assert.Single((await metadata.ListAsync()).Where(m => m.Reference != old));
        Assert.Equal(SecretReferenceState.Revoked, discarded.State);
        Assert.Null(await payloads.GetSecretAsync(discarded.Reference));
    }

    [Fact]
    public async Task MissingMetadataWithSurvivingPayload_StaysMissingUntilExplicitRotation()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, metadata);
        var saved = await lifecycle.CreateAsync("still-on-disk", SecretReferenceKind.ProviderHeader);
        await metadata.UpdateStateAsync(saved.Reference, SecretReferenceState.Missing, null);
        Assert.Equal("still-on-disk", await payloads.GetSecretAsync(saved.Reference));
        Assert.Equal(SecretReferenceState.Missing, (await lifecycle.GetStatusAsync(saved.Reference)).State);
        Assert.True((await lifecycle.RotateAsync(saved.Reference, "replacement")).IsUsable);
    }

    [Fact]
    public async Task ApiKeySaveDoesNotOverwriteWrongPurposeReference()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, metadata, repo);
        var gateway = await lifecycle.CreateAsync("gateway-original", SecretReferenceKind.GatewayApiKey);
        await repo.UpsertAsync(Profile(), gateway.Reference);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [], "provider-new");
        Assert.NotEqual(gateway.Reference, saved.ApiKeySecretReference);
        Assert.Equal("gateway-original", await payloads.GetSecretAsync(gateway.Reference));
        Assert.Equal(SecretReferenceKind.GatewayApiKey, (await lifecycle.GetStatusAsync(gateway.Reference)).Kind);
        Assert.Equal(SecretReferenceKind.ProviderApiKey, (await lifecycle.GetStatusAsync(saved.ApiKeySecretReference!)).Kind);
    }

    [Fact]
    public async Task DeletingOneProviderDoesNotRevokeAnotherProvidersSharedHeader()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "shared")]);
        var reference = saved.Profile.CustomHeaders![0].SecretReference!;
        await repo.UpsertAsync(Profile("second", headers: [new("X-Token", secretReference: reference)]));
        var owners = await new SqliteSecretReferenceRepository(db.Factory).ListOwnersAsync(reference);
        Assert.Contains(owners, owner => owner.OwnerKind == SecretReferenceOwnerKind.ProviderProfile && owner.OwnerId == "second");
        await lifecycle.DeleteProviderConfigurationAsync("provider", (await repo.GetByIdAsync("provider"))!.Revision!.Value);
        await repo.DeleteAsync("provider");
        Assert.True((await lifecycle.GetStatusAsync(reference)).IsUsable);
        await lifecycle.DeleteProviderConfigurationAsync("second", (await repo.GetByIdAsync("second"))!.Revision!.Value);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(reference)).State);
    }

    [Theory]
    [InlineData("ProviderHeader")]
    [InlineData("GatewayApiKey")]
    [InlineData("Unspecified")]
    [InlineData("unregistered")]
    [InlineData("foreign")]
    public async Task ApiKeyPurposeAndBindingGate_PreventsBothHttpPaths(string fault)
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, metadata, repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [], "synthetic-api-canary");
        var reference = saved.ApiKeySecretReference!;
        if (fault == "unregistered") await metadata.DeleteAsync(reference);
        else if (fault != "foreign")
        {
            await using var connection = await db.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE SecretReferences SET Kind=$kind;";
            command.Parameters.AddWithValue("$kind", fault);
            await command.ExecuteNonQueryAsync();
        }
        using var handler = new NoSendHandler();
        using var client = new HttpClient(handler);
        var id = fault == "foreign" ? "other" : "provider";
        var connectionResult = await new ProviderConnectionTestService(payloads, client, lifecycle, repo)
            .TestConnectionAsync(new(id, "Provider", "https://provider.test", reference));
        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, connectionResult.Status);
        var probe = await new ProviderModelProbeExecutor(payloads, client, lifecycle, repo).ExecuteAsync(new ModelProbeRequest
        {
            Scope = HealthScope.ForAccount("account"), ProviderProfileId = id, ModelId = "model",
            BaseUrl = "https://provider.test", ApiKeySecretReference = reference
        });
        Assert.Equal(ModelProbeOutcome.CredentialUnavailable, probe.Outcome);
        Assert.Null(probe.FailureClass);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task StatusLookupFailureAfterPayloadCreation_CompensatesItAndDoesNotExposeExceptionText()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var store = new InterceptingStore(payloads) { ThrowOnRead = true };
        var repo = new SqliteProviderProfileRepository(db.Factory);
        using var lifecycle = new SecretLifecycleService(store, metadata, repo);
        await Assert.ThrowsAsync<IOException>(() => lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "new-value")]));
        var entry = Assert.Single(await metadata.ListAsync());
        Assert.Equal(SecretReferenceState.Revoked, entry.State);
        Assert.Null(await payloads.GetSecretAsync(entry.Reference));
        Assert.Null(await repo.GetByIdAsync("provider"));
    }

    private sealed class InterceptingStore(ISecretStore inner) : ISecretStore
    {
        public Func<Task>? BeforeSave { get; set; }
        public bool ThrowOnRead { get; init; }
        public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            var before = BeforeSave; BeforeSave = null;
            if (before is not null) await before();
            return await inner.SaveSecretAsync(secret, cancellationToken);
        }
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) =>
            ThrowOnRead ? throw new IOException("synthetic-bare-secret-canary") : inner.GetSecretAsync(reference, cancellationToken);
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.DeleteSecretAsync(reference, cancellationToken);
    }

    private sealed class NoSendHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
    }

    private sealed class BlockingPayloadStore(DpapiSecretStore inner) : ISecretStore, ISecretPayloadManager
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveCount;
        public int SaveCount => Volatile.Read(ref _saveCount);
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.GetSecretAsync(reference, cancellationToken);
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.DeleteSecretAsync(reference, cancellationToken);
        public Task<bool> PayloadExistsAsync(string reference, CancellationToken cancellationToken = default) => inner.PayloadExistsAsync(reference, cancellationToken);
        public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.SaveSecretAsync(secret, cancellationToken);
        }
        public Task OverwriteSecretAsync(string reference, string secret, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A bound save must not overwrite the prior payload.");
    }
}

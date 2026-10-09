using LLMWorkGUI.Application.Security;
using System.Runtime.Versioning;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class BoundSecretReplacementTests
{
    private static ProviderProfile Profile() => new("provider", "Provider", BackendType.OpenCode,
        "https://provider.test", null, DataClassification.PrivateSource, true);
    private static Account Account(string? reference = null) => new("account", "provider", "Account", null,
        AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null, secretReference: reference);

    [Theory]
    [InlineData("api")]
    [InlineData("header")]
    [InlineData("account")]
    public async Task ReplacementCleanupFailure_CommitsNewBindingAndRecoversFromDurableQueue(string kind)
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        await profiles.UpsertAsync(Profile());
        await accounts.SaveAsync(Account());
        using var failing = new SecretLifecycleService(new FailedDeleteStore(payloads), metadata, profiles, accounts);
        async Task<string> Save(string value)
        {
            if (kind == "account") return (await failing.SaveAccountSecretAsync("account", value)).Reference;
            var profile = (await profiles.GetByIdAsync("provider"))!;
            var result = await failing.SaveProviderConfigurationAsync(profile,
                kind == "header" ? [new("X-Token", value)] : [], kind == "api" ? value : null);
            return kind == "header" ? result.Profile.CustomHeaders![0].SecretReference! : result.ApiKeySecretReference!;
        }
        var old = await Save("synthetic-old");
        var next = await Save("synthetic-next");
        Assert.NotEqual(old, next);
        Assert.Equal(SecretReferenceState.Revoked, (await metadata.GetAsync(old))!.State);
        Assert.Equal(old, Assert.Single(await profiles.ListPendingSecretDeletionsAsync()));
        Assert.Equal("synthetic-old", await payloads.GetSecretAsync(old));
        Assert.Equal("synthetic-next", await payloads.GetSecretAsync(next));
        Assert.False((await failing.GetStatusAsync(old)).IsUsable);
        Assert.True((await failing.GetStatusAsync(next)).IsUsable);
        if (kind == "account") Assert.Equal(next, await accounts.GetSecretReferenceAsync("account"));
        else if (kind == "api") Assert.Equal(next, await profiles.GetApiKeySecretReferenceAsync("provider"));
        else Assert.Equal(next, (await profiles.GetByIdAsync("provider"))!.CustomHeaders![0].SecretReference);

        using var recovered = new SecretLifecycleService(payloads, metadata, profiles, accounts);
        Assert.Equal(0, await recovered.RetryPendingSecretCleanupAsync());
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
        Assert.Null(await payloads.GetSecretAsync(old));
        Assert.Equal("synthetic-next", await payloads.GetSecretAsync(next));
    }

    [Fact]
    public async Task FailedSaveAndFailedCompensation_PreservesOldKeyAndRecoversDiscardedPayload()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var failing = new SecretLifecycleService(new FailedDeleteStore(payloads), metadata, profiles);
        var original = await failing.SaveProviderConfigurationAsync(Profile(), [], "synthetic-old");
        await Execute(db, "CREATE TRIGGER fail_save BEFORE UPDATE ON ProviderProfiles BEGIN SELECT RAISE(ABORT,'synthetic profile fault'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => failing.SaveProviderConfigurationAsync(original.Profile, [], "synthetic-discarded"));

        Assert.Equal(original.ApiKeySecretReference, await profiles.GetApiKeySecretReferenceAsync("provider"));
        Assert.True((await failing.GetStatusAsync(original.ApiKeySecretReference!)).IsUsable);
        Assert.Equal("synthetic-old", await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        var discarded = Assert.Single(await profiles.ListPendingSecretDeletionsAsync());
        Assert.NotEqual(original.ApiKeySecretReference, discarded);
        Assert.False((await failing.GetStatusAsync(discarded)).IsUsable);
        Assert.Equal("synthetic-discarded", await payloads.GetSecretAsync(discarded));
        using var recovered = new SecretLifecycleService(payloads, metadata, profiles);
        Assert.Equal(0, await recovered.RetryPendingSecretCleanupAsync());
        Assert.Null(await payloads.GetSecretAsync(discarded));
        Assert.Equal("synthetic-old", await payloads.GetSecretAsync(original.ApiKeySecretReference!));
    }

    [Fact]
    public async Task FailureDuringRetirement_RollsBackNewBindingAndPreservesOldPayload()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, metadata, profiles);
        var original = await lifecycle.SaveProviderConfigurationAsync(Profile(), [], "synthetic-old");
        await Execute(db, "CREATE TRIGGER fail_retirement BEFORE UPDATE ON SecretReferences BEGIN SELECT RAISE(ABORT,'synthetic metadata fault'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => lifecycle.SaveProviderConfigurationAsync(original.Profile, [], "synthetic-new"));

        Assert.Equal(original.ApiKeySecretReference, await profiles.GetApiKeySecretReferenceAsync("provider"));
        Assert.Equal(original.Profile.Revision, (await profiles.GetByIdAsync("provider"))!.Revision);
        Assert.Equal("synthetic-old", await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        Assert.True((await lifecycle.GetStatusAsync(original.ApiKeySecretReference!)).IsUsable);
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
        var discarded = Assert.Single((await metadata.ListAsync()).Where(item => item.Reference != original.ApiKeySecretReference));
        Assert.False((await lifecycle.GetStatusAsync(discarded.Reference)).IsUsable);
    }

    [Fact]
    public async Task RepositoryCommitBeforePayloadCleanup_QueuesRetirementAndKeepsSharedAccountWithoutOwnerMetadata()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        // Deliberately omit the account repository from the lifecycle: SQLite still sees real owners.
        using var lifecycle = new SecretLifecycleService(payloads, metadata, profiles);
        var original = await lifecycle.SaveProviderConfigurationAsync(Profile(), [], "synthetic-shared");
        await accounts.SaveAsync(Account(original.ApiKeySecretReference));
        var replaced = await lifecycle.SaveProviderConfigurationAsync(original.Profile, [], "synthetic-profile-only");
        Assert.True((await lifecycle.GetStatusAsync(original.ApiKeySecretReference!)).IsUsable);
        Assert.Equal("synthetic-shared", await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
        Assert.Empty((await metadata.ListOwnersAsync(original.ApiKeySecretReference!)).Where(owner => owner.OwnerKind == SecretReferenceOwnerKind.Account));

        // Stop exactly after a real repository commit: no lifecycle payload cleanup has run here.
        await accounts.SaveAsync(Account(replaced.ApiKeySecretReference));
        Assert.Equal(original.ApiKeySecretReference, Assert.Single(await profiles.ListPendingSecretDeletionsAsync()));
        Assert.Equal(SecretReferenceState.Revoked, (await metadata.GetAsync(original.ApiKeySecretReference!))!.State);
        Assert.Equal("synthetic-shared", await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        using var recovered = new SecretLifecycleService(payloads, metadata, profiles);
        Assert.Equal(0, await recovered.RetryPendingSecretCleanupAsync());
        Assert.Null(await payloads.GetSecretAsync(original.ApiKeySecretReference!));
        Assert.Equal("synthetic-profile-only", await payloads.GetSecretAsync(replaced.ApiKeySecretReference!));
    }

    private static async Task Execute(TestDatabase db, string sql)
    {
        await using var connection = await db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailedDeleteStore(ISecretStore inner) : ISecretStore, ISecretPayloadManager
    {
        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default) => inner.SaveSecretAsync(secret, cancellationToken);
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.GetSecretAsync(reference, cancellationToken);
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) => throw new IOException("synthetic payload cleanup fault");
        public async Task<bool> PayloadExistsAsync(string reference, CancellationToken cancellationToken = default) =>
            await inner.GetSecretAsync(reference, cancellationToken) is not null;
        public Task OverwriteSecretAsync(string reference, string secret, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic overwrite and rollback fault");
    }
}

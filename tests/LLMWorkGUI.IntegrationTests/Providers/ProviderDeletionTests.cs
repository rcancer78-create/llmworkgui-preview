using System.Runtime.Versioning;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class ProviderDeletionTests
{
    private static ProviderProfile Profile(string id = "provider", long? revision = null) =>
        new(id, "Provider", BackendType.OpenCode, "https://example.test", null, DataClassification.PrivateSource, true, revision: revision);

    [Theory]
    [InlineData("BEFORE DELETE ON ProviderProfiles")]
    [InlineData("BEFORE UPDATE ON SecretReferences")]
    public async Task DatabaseFault_RollsBackProfileRevocationAndQueueWithoutTouchingPayloads(string phase)
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "header")], "api");
        var header = saved.Profile.CustomHeaders![0].SecretReference!;
        await Execute(db, $"CREATE TRIGGER fault {phase} BEGIN SELECT RAISE(ABORT,'synthetic database fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => lifecycle.DeleteProviderConfigurationAsync("provider", saved.Profile.Revision!.Value));
        Assert.NotNull(await repo.GetByIdAsync("provider"));
        Assert.True((await lifecycle.GetStatusAsync(header)).IsUsable);
        Assert.True((await lifecycle.GetStatusAsync(saved.ApiKeySecretReference!)).IsUsable);
        Assert.Equal("header", await store.GetSecretAsync(header));
        Assert.Equal("api", await store.GetSecretAsync(saved.ApiKeySecretReference!));
        Assert.Empty(await repo.ListPendingSecretDeletionsAsync());
    }

    [Fact]
    public async Task PayloadFault_CommitsRevocationAndQueue_ThenNewLifecycleFinishesCleanup()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var store = new FailingDeleteStore(payloads);
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "header")], "api");
        var deleted = await lifecycle.DeleteProviderConfigurationAsync("provider", saved.Profile.Revision!.Value);
        Assert.True(deleted.Deleted);
        Assert.Equal(2, deleted.PendingSecretCleanup);
        Assert.Null(await repo.GetByIdAsync("provider"));
        var pending = await repo.ListPendingSecretDeletionsAsync();
        Assert.Equal(2, pending.Count);
        foreach (var reference in pending)
        {
            Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(reference)).State);
            Assert.NotNull(await payloads.GetSecretAsync(reference));
        }
        using var recovered = new SecretLifecycleService(payloads, new SqliteSecretReferenceRepository(db.Factory), new SqliteProviderProfileRepository(db.Factory));
        Assert.Equal(0, await recovered.RetryPendingSecretCleanupAsync());
        Assert.Empty(await repo.ListPendingSecretDeletionsAsync());
        foreach (var reference in pending) Assert.Null(await payloads.GetSecretAsync(reference));
    }

    [Fact]
    public async Task ProductionStartup_DrainsDeletionCommittedBeforePayloadCleanup()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "header")]);
        var reference = saved.Profile.CustomHeaders![0].SecretReference!;
        Assert.True(await repo.DeleteConfigurationAsync("provider", saved.Profile.Revision!.Value));
        Assert.NotNull(await payloads.GetSecretAsync(reference));
        Assert.Single(await repo.ListPendingSecretDeletionsAsync());
        using var host = HostBootstrapper.BuildHost(appDataDirectory: db.Root);
        await host.StartAsync(); await HostBootstrapper.InitializeAsync(host);
        Assert.Empty(await repo.ListPendingSecretDeletionsAsync());
        Assert.Null(await payloads.GetSecretAsync(reference));
        await host.StopAsync();
    }

    [Fact]
    public async Task StaleDelete_CannotRemoveNewerProfileOrItsCredentials()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "original")]);
        await repo.UpsertAsync(Profile(revision: saved.Profile.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.DeleteProviderConfigurationAsync("provider", saved.Profile.Revision!.Value));
        Assert.NotNull(await repo.GetByIdAsync("provider"));
        Assert.True((await lifecycle.GetStatusAsync(saved.Profile.CustomHeaders![0].SecretReference!)).IsUsable);
        Assert.Empty(await repo.ListPendingSecretDeletionsAsync());
    }

    [Fact]
    public async Task DeleteAndRecreateSameId_DoesNotReuseRevisionOrAcceptOldEditor()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var old = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "old")]);
        await lifecycle.DeleteProviderConfigurationAsync("provider", old.Profile.Revision!.Value);
        var current = await lifecycle.SaveProviderConfigurationAsync(Profile(revision: -1), [new("X-Token", "new")]);
        Assert.True(current.Profile.Revision > old.Profile.Revision);
        Assert.Equal(current.Profile.Revision, (await repo.GetByIdAsync("provider"))!.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.DeleteProviderConfigurationAsync("provider", old.Profile.Revision.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SaveProviderConfigurationAsync(old.Profile, [new("X-Token", "stale")]));
        Assert.Equal("new", await store.GetSecretAsync(current.Profile.CustomHeaders![0].SecretReference!));
    }

    [Theory]
    [InlineData("X-Signature")]
    [InlineData("X-Cert")]
    [InlineData("X-Pass")]
    public async Task SensitiveHeaderPolicy_StoresReferencesAndRedactsPreviewAndDiagnostics(string name)
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var input = new LLMWorkGUI.Application.Providers.CustomProviderHeader(name, "synthetic-private-header");
        Assert.True(input.IsSecret);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [input]);
        Assert.Null(saved.Profile.CustomHeaders![0].Value);
        Assert.NotNull(saved.Profile.CustomHeaders[0].SecretReference);
        var settings = new LLMWorkGUI.Application.Providers.CustomProviderSettings("provider", "Provider", "https://example.test", customHeaders: [input]);
        var report = await new LLMWorkGUI.Infrastructure.Providers.ProviderDiagnosticExportService().GenerateExportAsync(settings);
        Assert.DoesNotContain("synthetic-private-header", report.ToJson());
    }

    [Fact]
    public async Task MissingOwnerMetadata_DoesNotRevokeASurvivingAccountsSharedCredential()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderApiKeyAsync(Profile(), "shared");
        await repo.UpsertAsync(Profile("other"));
        await using var connection = await db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,SecretReference,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('survivor','other','Survivor','Unverified','Unknown',$ref,'2026-10-04','2026-10-04');
            DELETE FROM SecretReferenceOwners WHERE Reference=$ref;
            """;
        command.Parameters.AddWithValue("$ref", saved.Reference); await command.ExecuteNonQueryAsync();
        await lifecycle.DeleteProviderConfigurationAsync("provider", 0);
        Assert.True((await lifecycle.GetStatusAsync(saved.Reference)).IsUsable);
        Assert.Equal("shared", await store.GetSecretAsync(saved.Reference));
        Assert.Empty(await repo.ListPendingSecretDeletionsAsync());
        await lifecycle.DeleteProviderConfigurationAsync("other", 0);
        Assert.Null(await store.GetSecretAsync(saved.Reference));
    }

    [Fact]
    public async Task Migration18_BackfillsOwnersWithoutPromotingMissingOrWrongPurposeReferences()
    {
        using var db = new TestDatabase();
        await new DatabaseMigrator(db.Factory, DatabaseMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 17).ToArray()).MigrateAsync();
        await Execute(db, """
            INSERT INTO SecretReferences VALUES ('urn:llmworkgui:secret:legacy','Unspecified','Missing','2026-10-04',NULL,NULL);
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,ApiKeySecretReference,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('legacy','Legacy','OpenCode','PrivateSource','urn:llmworkgui:secret:legacy','2026-10-04','2026-10-04');
            """);
        Assert.Contains((await new DatabaseMigrator(db.Factory).MigrateAsync()).AppliedMigrations, m => m.Version == 18);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var reference = "urn:llmworkgui:secret:legacy";
        Assert.Equal("legacy", Assert.Single(await metadata.ListOwnersAsync(reference)).OwnerId);
        Assert.Equal(SecretReferenceState.Missing, (await metadata.GetAsync(reference))!.State);
        Assert.Equal(SecretReferenceKind.Unspecified, (await metadata.GetAsync(reference))!.Kind);
    }

    private static async Task Execute(TestDatabase db, string sql)
    {
        await using var connection = await db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    private sealed class FailingDeleteStore(ISecretStore inner) : ISecretStore
    {
        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default) => inner.SaveSecretAsync(secret, cancellationToken);
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.GetSecretAsync(reference, cancellationToken);
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) => throw new IOException("synthetic cleanup fault");
    }
}

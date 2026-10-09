using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class BackendAdaptationModelInvokerTests
{
    [Fact]
    public async Task SelectedAccountSetupUsesTheProductionRegistryAndNeverPromotesUnknownAuth()
    {
        using var f = await RuntimeFixture(configureMapping: false);
        await MaterialSql(f.Db, "UPDATE Accounts SET AuthState='Unknown'");
        var setup = f.Services.GetRequiredService<IAdaptationAccountConfigurationService>(); Assert.Same(f.Registry, setup);
        var before = await setup.ReadConfigurationAsync("provider-1", "account-1");
        Assert.True(before.IsKeyUsable); Assert.False(before.IsMappingCurrent); Assert.False(before.HasOwnedExecution);
        await setup.ConfigureProviderAsync(before, "openai");
        var configured = await setup.ReadConfigurationAsync("provider-1", "account-1");
        Assert.True(configured.IsMappingCurrent);
        Assert.Equal(AuthState.Unknown, (await f.Services.GetRequiredService<IAccountRepository>().GetByIdAsync("account-1"))!.AuthState);
        Assert.False(Directory.Exists(Path.Combine(f.Db.Root, "runs")));
    }

    [Fact]
    public async Task AccountKeyReplacementChangesOnlyItsReferenceAndRequiresExplicitMappingRenewal()
    {
        using var f = await RuntimeFixture(); var setup = f.Registry;
        var before = await setup.ReadConfigurationAsync("provider-1", "account-1");
        await MaterialSql(f.Db, "UPDATE Accounts SET AuthState='Unknown',ManualPriority=9,MaxConcurrentExecutions=3");
        await setup.SaveKeyAsync(before, "synthetic-new-account-key");
        var after = await setup.ReadConfigurationAsync("provider-1", "account-1");
        Assert.NotEqual(before.SecretReference, after.SecretReference); Assert.True(after.IsKeyUsable); Assert.False(after.IsMappingCurrent);
        Assert.Equal(before.SecretReference, after.MappingSecretReference);
        Assert.Equal(SecretReferenceState.Revoked, (await f.Secrets.GetStatusAsync(before.SecretReference!)).State);
        Assert.Null(await f.Services.GetRequiredService<ISecretStore>().GetSecretAsync(before.SecretReference!));
        var account = (await f.Services.GetRequiredService<IAccountRepository>().GetByIdAsync("account-1"))!;
        Assert.Equal(AuthState.Unknown, account.AuthState); Assert.Equal(9, account.ManualPriority); Assert.Equal(3, account.MaxConcurrentExecutions);
        await setup.ConfigureProviderAsync(after, "openai");
        Assert.True((await setup.ReadConfigurationAsync("provider-1", "account-1")).IsMappingCurrent);
    }

    [Fact]
    public async Task StaleAccountKeySnapshotAndProviderSnapshotRefuseWithoutMintingAnotherKey()
    {
        using var f = await RuntimeFixture(); var setup = f.Registry;
        var original = await setup.ReadConfigurationAsync("provider-1", "account-1");
        await setup.ConfigureProviderAsync(original, "openrouter");
        var refs = await f.Db.CountAsync("SecretReferences");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => setup.SaveKeyAsync(original, "synthetic-refused-key"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => setup.ConfigureProviderAsync(original, "openai"));
        Assert.Equal(refs, await f.Db.CountAsync("SecretReferences"));
        var current = await setup.ReadConfigurationAsync("provider-1", "account-1");
        await setup.SaveKeyAsync(current, "synthetic-replacement");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => setup.SaveKeyAsync(current, "synthetic-stale-key"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => setup.ConfigureProviderAsync(current, "openai"));
    }

    [Fact]
    public async Task WrongProfileSetupIsRefusedBeforeAnyCredentialMutation()
    {
        using var f = await RuntimeFixture(); var original = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.ReadConfigurationAsync("wrong-profile", "account-1"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.SaveKeyAsync(original with { ProviderProfileId = "wrong-profile" }, "synthetic-wrong-key"));
        Assert.Equal(original, await f.Registry.ReadConfigurationAsync("provider-1", "account-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionAdmittedOrSettingsChangedDuringPayloadMintCannotBeOverwrittenByTheKeyEditor(bool reserve)
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        var payloads = new BeforeReturnStore(f.Services.GetRequiredService<ISecretStore>(), async () =>
        {
            if (reserve)
            {
                await f.Db.SeedSessionAsync("racing-session", activeExecutionId: "racing-execution");
                await f.Db.SeedExecutionAsync("racing-execution", sessionId: "racing-session");
            }
            else await MaterialSql(f.Db, "UPDATE Accounts SET ManualPriority=11,AuthState='Unknown'");
        });
        using var lifecycle = new SecretLifecycleService(payloads, f.Services.GetRequiredService<ISecretReferenceRepository>(),
            f.Services.GetRequiredService<IProviderProfileRepository>(), f.Services.GetRequiredService<IAccountRepository>(),
            instanceGuard: f.Services.GetRequiredService<IApplicationInstanceGuard>());
        var save = () => lifecycle.SaveAccountSecretAsync("account-1", "synthetic-racing-editor-key", "provider-1", before.SecretReference);
        if (reserve)
        {
            await Assert.ThrowsAsync<SqliteException>(save);
            Assert.True((await f.Secrets.GetStatusAsync(before.SecretReference!)).IsUsable);
            Assert.Equal(before.SecretReference, (await f.Registry.ReadConfigurationAsync("provider-1", "account-1")).SecretReference);
            Assert.Null(await payloads.GetSecretAsync(payloads.CreatedReference!));
            await MaterialSql(f.Db, "UPDATE Executions SET EndedAtUtc='2026-10-05T00:00:00Z' WHERE Id='racing-execution'; UPDATE Sessions SET ActiveExecutionId=NULL WHERE Id='racing-session'");
        }
        else
        {
            await save();
            var account = (await f.Services.GetRequiredService<IAccountRepository>().GetByIdAsync("account-1"))!;
            Assert.Equal(11, account.ManualPriority); Assert.Equal(AuthState.Unknown, account.AuthState);
            Assert.NotEqual(before.SecretReference, account.SecretReference);
        }
    }

    [Fact]
    public async Task ConditionalAccountReferenceOperationNeverPersistsRawKeyText()
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Services.GetRequiredService<IAccountRepository>()
            .ReplaceSecretReferenceAsync("account-1", "provider-1", before.SecretReference, "synthetic-raw-key-value"));
        Assert.Equal(before, await f.Registry.ReadConfigurationAsync("provider-1", "account-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgementNeverRevokesTheKeyThatTheAccountAlreadyPointsAt(bool observationFails)
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        var accounts = new LostAcknowledgementAccounts(f.Services.GetRequiredService<IAccountRepository>(), observationFails);
        using var lifecycle = new SecretLifecycleService(f.Services.GetRequiredService<ISecretStore>(), f.Services.GetRequiredService<ISecretReferenceRepository>(),
            f.Services.GetRequiredService<IProviderProfileRepository>(), accounts, instanceGuard: f.Services.GetRequiredService<IApplicationInstanceGuard>());
        var save = () => lifecycle.SaveAccountSecretAsync("account-1", "synthetic-commit-ack-key", "provider-1", before.SecretReference);
        if (observationFails) await Assert.ThrowsAnyAsync<InvalidOperationException>(save); else await save();
        var current = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        Assert.NotEqual(before.SecretReference,current.SecretReference); Assert.True(current.IsKeyUsable); Assert.False(current.IsMappingCurrent);
        Assert.Equal(AuthState.Unknown, (await f.Services.GetRequiredService<IAccountRepository>().GetByIdAsync("account-1"))!.AuthState);
        Assert.NotNull(await f.Services.GetRequiredService<ISecretStore>().GetSecretAsync(current.SecretReference!));
        Assert.Equal(1L,await f.Db.CountAsync("SecretReferences","State='Active'"));
        if (observationFails) await f.Secrets.RetryPendingSecretCleanupAsync();
        Assert.Null(await f.Services.GetRequiredService<ISecretStore>().GetSecretAsync(before.SecretReference!));
    }

    private sealed class LostAcknowledgementAccounts(IAccountRepository inner, bool observationFails) : IAccountRepository
    {
        private bool _written;
        public Task<LLMWorkGUI.Domain.Entities.Account?> GetByIdAsync(string id, CancellationToken token = default)
            => _written && observationFails ? throw new IOException("synthetic account read failure") : inner.GetByIdAsync(id,token);
        public async Task ReplaceSecretReferenceAsync(string account, string profile, string? expected, string reference, CancellationToken token = default)
        { await inner.ReplaceSecretReferenceAsync(account,profile,expected,reference,token); _written=true; throw new IOException("synthetic lost commit acknowledgement"); }
        public Task<IReadOnlyList<LLMWorkGUI.Domain.Entities.Account>> ListByProviderProfileIdAsync(string profile, CancellationToken token = default) => inner.ListByProviderProfileIdAsync(profile,token);
        public Task<IReadOnlyList<LLMWorkGUI.Domain.Entities.Account>> ListAllAsync(CancellationToken token = default) => inner.ListAllAsync(token);
        public Task SaveAsync(LLMWorkGUI.Domain.Entities.Account account, CancellationToken token = default) => inner.SaveAsync(account,token);
        public Task DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id,token);
        public Task UpdateAuthStateAsync(string id, AuthState state, CancellationToken token = default) => inner.UpdateAuthStateAsync(id,state,token);
        public Task UpdateCooldownAsync(string id, DateTimeOffset? until, CancellationToken token = default) => inner.UpdateCooldownAsync(id,until,token);
        public Task<string?> GetSecretReferenceAsync(string id, CancellationToken token = default) => inner.GetSecretReferenceAsync(id,token);
    }

    private sealed class BeforeReturnStore(ISecretStore inner, Func<Task> beforeReturn) : ISecretStore
    {
        public string? CreatedReference;
        public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        { CreatedReference = await inner.SaveSecretAsync(secret, cancellationToken); await beforeReturn(); return CreatedReference; }
        public Task<string?> GetSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.GetSecretAsync(reference, cancellationToken);
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken cancellationToken = default) => inner.DeleteSecretAsync(reference, cancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("synthetic\nkey")]
    public async Task InvalidAccountKeyIsRefusedWithoutCreatingMetadata(string key)
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        var count = await f.Db.CountAsync("SecretReferences");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.SaveKeyAsync(before, key));
        Assert.Equal(count, await f.Db.CountAsync("SecretReferences"));
    }

    [Fact]
    public async Task ConditionalKeyBindingFailureCompensatesOnlyTheNewPayloadAndKeepsTheOldKeyUsable()
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureFailAccountKey BEFORE UPDATE OF SecretReference ON Accounts WHEN NEW.SecretReference IS NOT OLD.SecretReference BEGIN SELECT RAISE(ABORT,'synthetic conditional binding failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Registry.SaveKeyAsync(before, "synthetic-refused-new-key"));
        Assert.Equal(before, await f.Registry.ReadConfigurationAsync("provider-1", "account-1"));
        Assert.True((await f.Secrets.GetStatusAsync(before.SecretReference!)).IsUsable);
        Assert.Equal(1L, await f.Db.CountAsync("SecretReferences", "State='Active'"));
        Assert.Equal(1L, await f.Db.CountAsync("SecretReferences", "State='Revoked'"));
        await MaterialSql(f.Db, "DROP TRIGGER FixtureFailAccountKey");
    }

    [Fact]
    public async Task RetainedNativeOwnerPreventsKeyReplacementEvenThroughTheDirectRepositoryAndAllowsItAfterSameOwnerCleanup()
    {
        using var f = await RuntimeFixture(); var before = await f.Registry.ReadConfigurationAsync("provider-1", "account-1");
        await MaterialSql(f.Db, "CREATE TRIGGER FixtureHoldCleanup BEFORE INSERT ON WorkflowAdaptationRuntimeChecks WHEN NEW.Phase='Stopped' BEGIN SELECT RAISE(ABORT,'synthetic retained cleanup'); END;");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => PrepareAndInvokeAsync(f));
        Assert.True((await f.Registry.ReadConfigurationAsync("provider-1", "account-1")).HasOwnedExecution);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.SaveKeyAsync(before, "synthetic-held-key"));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Registry.ConfigureProviderAsync(before, "openai"));
        await Assert.ThrowsAsync<SqliteException>(() => f.Secrets.SaveAccountSecretAsync("account-1", "synthetic-racing-key", "provider-1", before.SecretReference));
        await Assert.ThrowsAsync<SqliteException>(() => MaterialSql(f.Db, "UPDATE Accounts SET SecretReference=NULL WHERE Id='account-1'"));
        Assert.True((await f.Secrets.GetStatusAsync(before.SecretReference!)).IsUsable);
        await MaterialSql(f.Db, "DROP TRIGGER FixtureHoldCleanup");
        Assert.Equal(0, await f.Registry.RetryOwnedCleanupAsync());
        var released = await f.Registry.ReadConfigurationAsync("provider-1", "account-1"); Assert.False(released.HasOwnedExecution);
        await f.Registry.SaveKeyAsync(released, "synthetic-after-cleanup-key");
        Assert.True((await f.Registry.ReadConfigurationAsync("provider-1", "account-1")).IsKeyUsable);
    }
}

using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Security;

/// <summary>
/// Covers the ADR-0005 §5 lifecycle on top of an in-memory store and metadata table: which URN a
/// value ends up behind, when a payload may still be used, and what a replacement leaves behind.
/// </summary>
public sealed class SecretLifecycleServiceTests
{
    private const string ProviderId = "provider-1";
    private const string AccountId = "account-1";

    [Fact]
    public async Task ProviderRevocationCannotReportSuccessWhileAnotherOwnerStillUsesTheCredential()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);
        var created = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-shared-credential");
        await accounts.SetSecretReferenceAsync(AccountId, created.Reference);
        await lifecycle.BindAsync(created.Reference, SecretReferenceOwnerBinding.ForAccount(created.Reference, AccountId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RevokeProviderApiKeyAsync(ProviderId));
        Assert.Equal(created.Reference, await profiles.GetApiKeySecretReferenceAsync(ProviderId));
        Assert.Equal(created.Reference, await accounts.GetSecretReferenceAsync(AccountId));
        Assert.Equal("synthetic-shared-credential", await store.GetSecretAsync(created.Reference));
        Assert.Equal(SecretReferenceState.Active, (await lifecycle.GetStatusAsync(created.Reference)).State);
    }

    [Fact]
    public async Task CreateAsync_RegistersActiveReferenceWithOwner()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var status = await lifecycle.CreateAsync(
            "  sk-first-value  ",
            SecretReferenceKind.ProviderApiKey,
            SecretReferenceOwnerBinding.ForProviderProfile(SecretReference.Create("placeholder"), ProviderId));

        Assert.True(status.IsUsable);
        Assert.Equal(SecretReferenceState.Active, status.State);
        Assert.True(status.IsRegistered);
        Assert.True(SecretReference.IsValid(status.Reference));
        Assert.Equal(SecretReferenceKind.ProviderApiKey, status.Kind);

        // The value is trimmed before it is stored and the raw value never becomes part of a URN.
        Assert.Equal("sk-first-value", await store.GetSecretAsync(status.Reference));

        var owners = await references.ListOwnersAsync(status.Reference);
        Assert.Equal(ProviderId, Assert.Single(owners).OwnerId);
    }

    [Fact]
    public async Task RotateAsync_OverwritesTheSameReferenceAndRecordsTheRotation()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        using var lifecycle = CreateLifecycle(store, references, timeProvider: clock);

        var created = await lifecycle.CreateAsync("sk-original");
        clock.UtcNow = clock.UtcNow.AddDays(1);

        var rotated = await lifecycle.RotateAsync(created.Reference, "sk-rotated");

        // ADR-0005 §5.1: the rotation overwrites the value at the same target, so every row that
        // references the URN keeps working and no second reference is created.
        Assert.Equal(created.Reference, rotated.Reference);
        Assert.Single(store.References);
        Assert.Equal("sk-rotated", await store.GetSecretAsync(created.Reference));
        Assert.Equal(clock.GetUtcNow(), rotated.LastRotatedAtUtc);
    }

    [Fact]
    public async Task RevokeAsync_RecordsRevocationBeforeRemovingThePayload()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync("sk-to-revoke");
        var revoked = await lifecycle.RevokeAsync(created.Reference);

        Assert.Equal(SecretReferenceState.Revoked, revoked.State);
        Assert.False(revoked.IsUsable);
        Assert.Null(await store.GetSecretAsync(created.Reference));
        Assert.NotNull(await references.GetAsync(created.Reference));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("metadata")]
    [InlineData("delete")]
    public async Task RevokeAsync_PersistsRevocationBeforeDeletingPayloadAndRemainsSafeOnPartialFailure(string failure)
    {
        var operations = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);
        var created = await lifecycle.CreateAsync("synthetic-revocation-payload");
        references.BeforeStateUpdate = async () =>
        {
            operations.Add("metadata-start");
            entered.TrySetResult();
            await commit.Task;
            operations.Add("metadata-committed");
        };
        store.BeforeDelete = () =>
        {
            operations.Add("payload-delete");
            if (failure == "delete") throw new IOException("synthetic delete failure");
        };
        var revocation = lifecycle.RevokeAsync(created.Reference);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "metadata-start" }, operations);
            Assert.True(await store.PayloadExistsAsync(created.Reference));
            if (failure == "metadata") commit.TrySetException(new IOException("synthetic metadata failure"));
            else commit.TrySetResult();

            if (failure == "none") await revocation.WaitAsync(TimeSpan.FromSeconds(5));
            else await Assert.ThrowsAsync<IOException>(() => revocation);

            var status = await lifecycle.GetStatusAsync(created.Reference);
            Assert.Equal(failure == "metadata" ? SecretReferenceState.Active : SecretReferenceState.Revoked, status.State);
            Assert.Equal(failure == "metadata", status.IsUsable);
            Assert.Equal(failure != "none", await store.PayloadExistsAsync(created.Reference));
            Assert.Equal(failure == "metadata" ? new[] { "metadata-start" }
                : new[] { "metadata-start", "metadata-committed", "payload-delete" }, operations);
        }
        finally
        {
            commit.TrySetResult();
            try { await revocation; } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RevokedReference_StaysUnusableWhenThePayloadSurvives()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync("sk-to-revoke");
        await lifecycle.RevokeAsync(created.Reference);

        // A payload that outlives the revocation (an interrupted delete, a restored backup) must not
        // make the URN usable again: the recorded state wins over the file.
        store.RestorePayload(created.Reference, "sk-surviving-value");

        var status = await lifecycle.GetStatusAsync(created.Reference);

        Assert.Equal(SecretReferenceState.Revoked, status.State);
        Assert.False(status.IsUsable);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsMissingWhenThePayloadIsGone()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync("sk-present");
        await store.DeleteSecretAsync(created.Reference);

        var status = await lifecycle.GetStatusAsync(created.Reference);

        Assert.Equal(SecretReferenceState.Missing, status.State);
        Assert.False(status.IsUsable);
        Assert.True(status.IsRegistered);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsMalformedLegacyReferenceAsMissing()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        // The migration deliberately skips a legacy value that fails the canonical URN contract, so
        // the lifecycle has to fail closed on it instead of throwing or trusting it.
        const string legacy = "urn:llmworkgui:secret:Legacy.Key";
        store.RestorePayload(legacy, "sk-legacy-value");

        var status = await lifecycle.GetStatusAsync(legacy);

        Assert.Equal(SecretReferenceState.Missing, status.State);
        Assert.False(status.IsUsable);
    }

    [Fact]
    public async Task RevokeAsync_RegistersRevocationForAnUnregisteredReference()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync("sk-unregistered");
        await references.DeleteAsync(created.Reference);
        store.RestorePayload(created.Reference, "sk-unregistered-value");

        var revoked = await lifecycle.RevokeAsync(created.Reference);

        Assert.Equal(SecretReferenceState.Revoked, revoked.State);
        Assert.Null(await store.GetSecretAsync(created.Reference));
        var durable = Assert.IsType<SecretReferenceMetadata>(await references.GetAsync(created.Reference));
        Assert.Equal(SecretReferenceState.Revoked, durable.State);
        Assert.NotNull(durable.RevokedAtUtc);
        store.RestorePayload(created.Reference, "synthetic-surviving-revoked-payload");
        var restored = await lifecycle.GetStatusAsync(created.Reference);
        Assert.True(restored.IsRegistered);
        Assert.Equal(SecretReferenceState.Revoked, restored.State);
        Assert.False(restored.IsUsable);
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_CommitsNewReferenceAndRetiresOldPayload()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles);
        var profile = CreateProfile();

        var first = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-first");
        profiles.SecretReferences[ProviderId] = first.Reference;

        var second = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-second");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Single(store.References);
        Assert.Null(await store.GetSecretAsync(first.Reference));
        Assert.Equal("sk-second", await store.GetSecretAsync(second.Reference));
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(first.Reference)).State);
        Assert.True(second.IsUsable);
        Assert.Equal(SecretReferenceState.Active, second.State);
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_RegistersNewReferenceForAProfileWithoutOne()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles);

        var status = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "sk-first");

        Assert.True(SecretReference.IsValid(status.Reference));
        Assert.Equal(status.Reference, profiles.SecretReferences[ProviderId]);
        Assert.Equal(ProviderId, (await references.ListOwnersAsync(status.Reference)).Single().OwnerId);
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_NeverReusesARevokedReference()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles);
        var profile = CreateProfile();

        var first = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-first");
        await lifecycle.RevokeProviderApiKeyAsync(ProviderId);
        store.RestorePayload(first.Reference, "sk-surviving-value");

        var second = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-second");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Equal(second.Reference, profiles.SecretReferences[ProviderId]);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(first.Reference)).State);
        Assert.Null(await store.GetSecretAsync(first.Reference));
        Assert.Equal("sk-second", await store.GetSecretAsync(second.Reference));
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_CompensatesTheNewReferenceWhenTheProfileCannotBePersisted()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository { FailOnUpsert = true };
        using var lifecycle = CreateLifecycle(store, references, profiles);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "sk-first"));

        // Nothing may be left behind: no payload that nothing can reach, and no reference that is
        // still usable after the operation failed.
        Assert.Empty(store.References);
        Assert.Null(profiles.SecretReferences.GetValueOrDefault(ProviderId));

        var compensated = Assert.Single(await references.ListAsync());
        Assert.Equal(SecretReferenceState.Revoked, compensated.State);
        Assert.False((await lifecycle.GetStatusAsync(compensated.Reference)).IsUsable);
    }

    [Fact]
    public async Task SaveAccountSecretAsync_CommitsNewReferenceAndRetiresOldPayload()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, accounts: accounts);

        var first = await lifecycle.SaveAccountSecretAsync(AccountId, "sk-account-first");
        var second = await lifecycle.SaveAccountSecretAsync(AccountId, "sk-account-second");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Null(await store.GetSecretAsync(first.Reference));
        Assert.Single(store.References);
        Assert.Equal(second.Reference, await accounts.GetSecretReferenceAsync(AccountId));
        Assert.Equal(AccountId, (await references.ListOwnersAsync(second.Reference)).Single().OwnerId);
    }

    [Fact]
    public async Task RevokeAccountSecretAsync_MakesTheAccountCredentialUnusable()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, accounts: accounts);

        var created = await lifecycle.SaveAccountSecretAsync(AccountId, "sk-account");
        await lifecycle.RevokeAccountSecretAsync(AccountId);

        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(created.Reference)).State);
        Assert.Null(await store.GetSecretAsync(created.Reference));
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_ReplacesANonCanonicalLegacyReference()
    {
        // A profile whose column still holds a value the canonical URN contract rejects must be
        // repairable by entering the key again instead of failing the save.
        const string legacy = "urn:llmworkgui:secret:Legacy.Key";
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository { SecretReferences = { [ProviderId] = legacy } };
        using var lifecycle = CreateLifecycle(store, references, profiles);

        var status = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "sk-fresh-value");

        Assert.True(SecretReference.IsValid(status.Reference));
        Assert.NotEqual(legacy, status.Reference);
        Assert.Equal(status.Reference, profiles.SecretReferences[ProviderId]);
        Assert.Equal("sk-fresh-value", await store.GetSecretAsync(status.Reference));
        Assert.Empty(await references.ListOwnersAsync(legacy));
    }

    [Fact]
    public async Task SharedReference_StaysUsableForItsOtherOwnerWhenOneOwnerSavesAgain()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);

        // Updating this profile must preserve the other account's binding AND its value.
        var created = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "sk-shared");
        await lifecycle.BindAsync(created.Reference, SecretReferenceOwnerBinding.ForAccount(created.Reference, AccountId));
        await accounts.SetSecretReferenceAsync(AccountId, created.Reference);

        var rotated = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "sk-shared-rotated");

        Assert.NotEqual(created.Reference, rotated.Reference);
        Assert.Equal("sk-shared", await store.GetSecretAsync(created.Reference));
        Assert.Equal("sk-shared-rotated", await store.GetSecretAsync(rotated.Reference));
        var owners = await references.ListOwnersAsync(created.Reference);
        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, owner => owner.OwnerId == AccountId);
    }

    [Fact]
    public async Task RotateAsync_RefusesARevokedReference()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync("sk-revoked");
        await lifecycle.RevokeAsync(created.Reference);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.RotateAsync(created.Reference, "sk-attempted"));
    }

    [Fact]
    public async Task RotateAsync_WhenStoreCannotOverwriteInPlace_MovesTheValueToANewReference()
    {
        // A store that only implements ISecretStore cannot rotate at the same target, so the value
        // must move instead of being reported as an in-place rotation that did not happen.
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = new SecretLifecycleService(store, references);

        var created = await lifecycle.CreateAsync("sk-original");
        var rotated = await lifecycle.RotateAsync(created.Reference, "sk-rotated");

        Assert.NotEqual(created.Reference, rotated.Reference);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(created.Reference)).State);
        Assert.Equal("sk-rotated", await store.GetSecretAsync(rotated.Reference));
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_WhenStoreCannotOverwriteInPlace_BindsTheNewReference()
    {
        // The value moves to a new URN, so the ownership has to follow it: a reference without an
        // owner could never be found again for this profile.
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = new SecretLifecycleService(store, references, profiles);
        var profile = CreateProfile();

        var first = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-first");
        profiles.SecretReferences[ProviderId] = first.Reference;

        var second = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-second");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Equal(second.Reference, profiles.SecretReferences[ProviderId]);
        Assert.Equal(ProviderId, Assert.Single(await references.ListOwnersAsync(second.Reference)).OwnerId);

        // The superseded reference is revoked only after the profile points at its replacement.
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(first.Reference)).State);
        Assert.Null(await store.GetSecretAsync(first.Reference));
    }

    [Fact]
    public async Task SaveProviderApiKeyAsync_WhenFallbackBindingFails_KeepsThePreviousKeyUsable()
    {
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = new SecretLifecycleService(store, references, profiles);
        var profile = CreateProfile();

        var first = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-first");
        profiles.SecretReferences[ProviderId] = first.Reference;
        profiles.FailOnUpsert = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SaveProviderApiKeyAsync(profile, "sk-second"));

        // The failure must not destroy the working key: the old payload and its Active metadata stay,
        // and only the replacement this save created is compensated.
        var status = await lifecycle.GetStatusAsync(first.Reference);

        Assert.Equal(SecretReferenceState.Active, status.State);
        Assert.True(status.IsUsable);
        Assert.Equal("sk-first", await store.GetSecretAsync(first.Reference));
        Assert.Equal(first.Reference, profiles.SecretReferences[ProviderId]);

        var replacement = Assert.Single(
            (await references.ListAsync()).Where(metadata => metadata.Reference != first.Reference));

        Assert.Equal(SecretReferenceState.Revoked, replacement.State);
        Assert.Null(await store.GetSecretAsync(replacement.Reference));
    }

    [Fact]
    public async Task SaveAccountSecretAsync_WhenFallbackBindingFails_KeepsThePreviousKeyUsable()
    {
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = new SecretLifecycleService(store, references, accounts: accounts);

        var first = await lifecycle.SaveAccountSecretAsync(AccountId, "sk-account-first");
        accounts.FailOnSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SaveAccountSecretAsync(AccountId, "sk-account-second"));

        // The account still resolves to a usable credential, exactly as before the failed save.
        Assert.Equal(SecretReferenceState.Active, (await lifecycle.GetStatusAsync(first.Reference)).State);
        Assert.Equal("sk-account-first", await store.GetSecretAsync(first.Reference));
        Assert.Equal(first.Reference, await accounts.GetSecretReferenceAsync(AccountId));

        var replacement = Assert.Single(
            (await references.ListAsync()).Where(metadata => metadata.Reference != first.Reference));

        Assert.Equal(SecretReferenceState.Revoked, replacement.State);
        Assert.Null(await store.GetSecretAsync(replacement.Reference));
    }

    [Fact]
    public async Task SaveAccountSecretAsync_ConcurrentSaves_LeaveNoOrphanedActiveReference()
    {
        // A store that cannot overwrite mints a replacement URN for every save and yields while doing
        // so, so the two calls really do overlap between minting a replacement and binding it. Both
        // must take the reference they supersede from the row they read themselves.
        var store = new SaveOnlySecretStore(yieldOnSave: true);
        var references = new FakeSecretReferenceRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = new SecretLifecycleService(store, references, accounts: accounts);

        var original = await lifecycle.SaveAccountSecretAsync(AccountId, "sk-original");

        await Task.WhenAll(
            lifecycle.SaveAccountSecretAsync(AccountId, "sk-concurrent-a"),
            lifecycle.SaveAccountSecretAsync(AccountId, "sk-concurrent-b"));

        var current = await accounts.GetSecretReferenceAsync(AccountId);

        Assert.NotNull(current);
        Assert.True((await lifecycle.GetStatusAsync(current!)).IsUsable);

        // Exactly one reference may stay usable: a second one would be a payload with an owner that
        // the account no longer points at.
        var active = (await references.ListAsync())
            .Where(metadata => metadata.State == SecretReferenceState.Active)
            .ToArray();

        Assert.Equal(current, Assert.Single(active).Reference);
        Assert.Single(store.References);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(original.Reference)).State);
    }

    [Fact]
    public async Task CreateAsync_WithoutPayloadInspection_ReportsTheReferenceAsUsable()
    {
        // The original store contract can still resolve the saved value, so absence of
        // the optional presence/overwrite interface must not disable a valid reference.
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = new SecretLifecycleService(store, references);

        var created = await lifecycle.CreateAsync("sk-first");

        Assert.Equal(SecretReferenceState.Active, (await lifecycle.GetStatusAsync(created.Reference)).State);
    }

    [Fact]
    public async Task GetStatusAsync_WithoutPayloadManager_DoesNotInventAnUnknownReference()
    {
        var store = new SaveOnlySecretStore();
        using var lifecycle = new SecretLifecycleService(store, new FakeSecretReferenceRepository());

        var status = await lifecycle.GetStatusAsync(SecretReference.Create("synthetic-missing"));

        Assert.Equal(SecretReferenceState.Missing, status.State);
        Assert.False(status.IsUsable);
        Assert.False(status.IsRegistered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetStatusAsync_WithoutPayloadManager_DeletedPayloadIsUnusable(bool registered)
    {
        var store = new SaveOnlySecretStore();
        using var lifecycle = new SecretLifecycleService(store, new FakeSecretReferenceRepository());
        var reference = registered
            ? (await lifecycle.CreateAsync("synthetic-value")).Reference
            : await store.SaveSecretAsync("synthetic-value");
        Assert.True(await store.DeleteSecretAsync(reference));

        var status = await lifecycle.GetStatusAsync(reference);

        Assert.Equal(SecretReferenceState.Missing, status.State);
        Assert.False(status.IsUsable);
        Assert.Equal(registered, status.IsRegistered);
    }

    [Fact]
    public async Task CreateAsync_RemovesThePayloadWhenTheMetadataCannotBeWritten()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository { FailOnInsert = true };
        using var lifecycle = CreateLifecycle(store, references);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.CreateAsync("sk-orphan"));

        Assert.Empty(store.References);
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptySecret()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        await Assert.ThrowsAsync<ArgumentException>(() => lifecycle.CreateAsync("   "));
        Assert.Empty(store.References);
        Assert.Empty(await references.ListAsync());
    }

    [Fact]
    public async Task CancelledOperation_DoesNotReportASuccessfulLifecycleResult()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);
        var created = await lifecycle.CreateAsync("sk-original");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.RotateAsync(created.Reference, "sk-rotated", cancelled.Token));

        // The rotation never happened, so the original value and its metadata must be untouched.
        Assert.Equal("sk-original", await store.GetSecretAsync(created.Reference));
        Assert.Null((await references.GetAsync(created.Reference))!.LastRotatedAtUtc);
    }

    [Fact]
    public async Task ConcurrentRotations_LeaveOneConsistentValueAndMetadata()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);
        var created = await lifecycle.CreateAsync("sk-original");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            lifecycle.RotateAsync(created.Reference, $"sk-value-{index}")));

        var final = await lifecycle.GetStatusAsync(created.Reference);
        var finalValue = await store.GetSecretAsync(created.Reference);

        Assert.Equal(SecretReferenceState.Active, final.State);
        Assert.True(final.IsUsable);
        Assert.NotNull(final.LastRotatedAtUtc);

        // Overlapping rotations must not leave a partial or unresolvable payload behind: the file
        // always holds exactly one of the values that were written.
        Assert.NotNull(finalValue);
        Assert.StartsWith("sk-value-", finalValue, StringComparison.Ordinal);
        Assert.Single(store.References);
    }

    [Fact]
    public async Task BindAsync_RecordsASecondOwnerForASharedReference()
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);

        var created = await lifecycle.CreateAsync(
            "sk-shared",
            SecretReferenceKind.ProviderApiKey,
            SecretReferenceOwnerBinding.ForProviderProfile(SecretReference.Create("placeholder"), ProviderId));

        var bound = await lifecycle.BindAsync(
            created.Reference,
            SecretReferenceOwnerBinding.ForAccount(created.Reference, AccountId));

        Assert.True(bound.IsUsable);
        Assert.Equal(2, (await references.ListOwnersAsync(created.Reference)).Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveBoundSecret_WhenMetadataOrOwnerSaveFails_PreservesPreviousPayload(
        bool accountOwner, bool metadataFailure)
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);
        Task<SecretReferenceStatus> Save(string value) => accountOwner
            ? lifecycle.SaveAccountSecretAsync(AccountId, value)
            : lifecycle.SaveProviderApiKeyAsync(CreateProfile(), value);
        var original = await Save("synthetic-original");
        references.FailOnInsert = metadataFailure;
        accounts.FailOnSave = !metadataFailure && accountOwner;
        profiles.FailOnUpsert = !metadataFailure && !accountOwner;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Save("synthetic-replacement"));

        var bound = accountOwner
            ? await accounts.GetSecretReferenceAsync(AccountId)
            : await profiles.GetApiKeySecretReferenceAsync(ProviderId);
        Assert.Equal(original.Reference, bound);
        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        Assert.Single(store.References);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveBoundSecret_WhenFallbackReplacesSharedReference_PreservesOtherOwner(bool accountOwner)
    {
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);
        var original = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-shared");
        await accounts.SetSecretReferenceAsync(AccountId, original.Reference);
        await lifecycle.BindAsync(original.Reference,
            SecretReferenceOwnerBinding.ForAccount(original.Reference, AccountId));

        var replacement = accountOwner
            ? await lifecycle.SaveAccountSecretAsync(AccountId, "synthetic-new-owner-value")
            : await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-new-owner-value");

        Assert.NotEqual(original.Reference, replacement.Reference);
        var otherOwnerReference = accountOwner
            ? await profiles.GetApiKeySecretReferenceAsync(ProviderId)
            : await accounts.GetSecretReferenceAsync(AccountId);
        Assert.Equal(original.Reference, otherOwnerReference);
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        Assert.Equal("synthetic-shared", await store.GetSecretAsync(original.Reference));
        Assert.Equal("synthetic-new-owner-value", await store.GetSecretAsync(replacement.Reference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveBoundSecret_WhenFallbackOwnerBindingFails_RemovesUnboundReplacement(bool accountOwner)
    {
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);
        Task<SecretReferenceStatus> Save(string value) => accountOwner
            ? lifecycle.SaveAccountSecretAsync(AccountId, value)
            : lifecycle.SaveProviderApiKeyAsync(CreateProfile(), value);
        var original = await Save("synthetic-original");
        references.FailOnAddOwner = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Save("synthetic-replacement"));

        Assert.Equal(original.Reference, Assert.Single(store.References));
        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        Assert.Single((await references.ListAsync()).Where(item => item.State == SecretReferenceState.Active));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOwnerSave_RollbackIgnoresCancellationAndPreservesOriginalError(bool rollbackFails)
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles);
        var original = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-original");
        using var cancellation = new CancellationTokenSource();
        profiles.FailOnUpsert = true;
        profiles.OnUpsertFailure = () =>
        {
            cancellation.Cancel();
            if (rollbackFails)
                store.BeforeDelete = () => throw new IOException("synthetic replacement cleanup failure");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-replacement", cancellation.Token));

        Assert.Equal("Simulated profile persistence failure.", error.Message);
        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        Assert.Equal(original.Reference, await profiles.GetApiKeySecretReferenceAsync(ProviderId));
        var replacement = Assert.Single((await references.ListAsync()).Where(item => item.Reference != original.Reference));
        Assert.Equal(SecretReferenceState.Revoked, replacement.State);
        Assert.False((await lifecycle.GetStatusAsync(replacement.Reference)).IsUsable);
        Assert.Equal(rollbackFails ? "synthetic-replacement" : null, await store.GetSecretAsync(replacement.Reference));
        Assert.NotEmpty(store.DeleteTokens);
        Assert.All(store.DeleteTokens, token => Assert.Equal(CancellationToken.None, token));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("metadata")]
    [InlineData("owner")]
    public async Task CancelledBoundSave_AfterMutationBeforeOwnerCommitCompensatesReplacement(string boundary)
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles);
        var original = await lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-original");
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PauseAfterCommittedMutation()
        {
            entered.TrySetResult();
            await release.Task;
        }
        // The dependency returns its committed mutation even after cancellation. The next
        // token-aware lifecycle operation must compensate without the cancelled owner token.
        if (boundary == "payload") store.AfterSave = PauseAfterCommittedMutation;
        else if (boundary == "metadata") references.AfterInsert = PauseAfterCommittedMutation;
        else references.AfterAddOwner = PauseAfterCommittedMutation;
        var operation = lifecycle.SaveProviderApiKeyAsync(CreateProfile(), "synthetic-replacement", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(operation.IsCompleted);
            await cancellation.CancelAsync();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }

        Assert.Equal(original.Reference, await profiles.GetApiKeySecretReferenceAsync(ProviderId));
        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        var replacement = Assert.Single((await references.ListAsync()).Where(item => item.Reference != original.Reference));
        Assert.Equal(SecretReferenceState.Revoked, replacement.State);
        Assert.False((await lifecycle.GetStatusAsync(replacement.Reference)).IsUsable);
        Assert.Null(await store.GetSecretAsync(replacement.Reference));
        Assert.NotEmpty(store.DeleteTokens);
        Assert.All(store.DeleteTokens, token => Assert.Equal(CancellationToken.None, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBoundSave_WithFailedReplacementCleanup_PreservesOldValueAndBinding(bool accountOwner)
    {
        var store = new FakeSecretStore();
        var references = new FakeSecretReferenceRepository();
        var profiles = new FakeProviderProfileRepository();
        var accounts = new FakeAccountRepository();
        using var lifecycle = CreateLifecycle(store, references, profiles, accounts);
        Task<SecretReferenceStatus> Save(string value) => accountOwner
            ? lifecycle.SaveAccountSecretAsync(AccountId, value)
            : lifecycle.SaveProviderApiKeyAsync(CreateProfile(), value);
        var original = await Save("synthetic-original");
        profiles.FailOnUpsert = !accountOwner;
        accounts.FailOnSave = accountOwner;
        store.FailOnOverwrite = true;
        store.BeforeDelete = () => throw new IOException("synthetic cleanup failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Save("synthetic-replacement"));

        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.True((await lifecycle.GetStatusAsync(original.Reference)).IsUsable);
        Assert.Equal(original.Reference, accountOwner
            ? await accounts.GetSecretReferenceAsync(AccountId)
            : await profiles.GetApiKeySecretReferenceAsync(ProviderId));
        var discarded = Assert.Single((await references.ListAsync()).Where(item => item.Reference != original.Reference));
        Assert.Equal(SecretReferenceState.Revoked, discarded.State);
        Assert.False((await lifecycle.GetStatusAsync(discarded.Reference)).IsUsable);
    }

    [Fact]
    public async Task RotateSaveOnlyStore_FailedSupersededDeletionCompensatesTheUnreturnedReplacement()
    {
        var store = new SaveOnlySecretStore();
        var references = new FakeSecretReferenceRepository();
        using var lifecycle = CreateLifecycle(store, references);
        var original = await lifecycle.CreateAsync("synthetic-original");
        var failure = new IOException("synthetic superseded deletion failure");
        store.BeforeDelete = reference => { if (reference == original.Reference) throw failure; };

        var error = await Assert.ThrowsAsync<IOException>(() => lifecycle.RotateAsync(original.Reference, "synthetic-replacement"));

        Assert.Same(failure, error);
        Assert.Equal("synthetic-original", await store.GetSecretAsync(original.Reference));
        Assert.Equal(SecretReferenceState.Revoked, (await references.GetAsync(original.Reference))!.State);
        var replacement = Assert.Single((await references.ListAsync()).Where(item => item.Reference != original.Reference));
        Assert.Equal(SecretReferenceState.Revoked, replacement.State);
        Assert.False((await lifecycle.GetStatusAsync(replacement.Reference)).IsUsable);
        Assert.Null(await store.GetSecretAsync(replacement.Reference));
    }

    private static SecretLifecycleService CreateLifecycle(
        ISecretStore store,
        ISecretReferenceRepository references,
        FakeProviderProfileRepository? profiles = null,
        FakeAccountRepository? accounts = null,
        TimeProvider? timeProvider = null) =>
        new(store, references, profiles, accounts, timeProvider);

    private static ProviderProfile CreateProfile() =>
        new(ProviderId, "Provider 1", BackendType.OpenCode, "http://127.0.0.1:11434/v1", null, DataClassification.PrivateSource, true);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public FixedTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeSecretStore : ISecretStore, ISecretPayloadManager
    {
        public Dictionary<string, string> References { get; } = new(StringComparer.Ordinal);
        public bool FailOnOverwrite { get; set; }
        public Action? BeforeDelete { get; set; }
        public Func<Task>? AfterSave { get; set; }
        public List<CancellationToken> DeleteTokens { get; } = [];

        public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reference = SecretReference.Create(Guid.NewGuid().ToString("N"));
            References[reference] = secret;
            if (AfterSave is not null) await AfterSave();
            return reference;
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(References.TryGetValue(secretReference, out var secret) ? secret : null);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            DeleteTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BeforeDelete?.Invoke();

            return Task.FromResult(References.Remove(secretReference));
        }

        public Task<bool> PayloadExistsAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(References.ContainsKey(secretReference));
        }

        public Task OverwriteSecretAsync(string secretReference, string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FailOnOverwrite)
            {
                throw new IOException("Simulated rollback write failure.");
            }

            References[secretReference] = secret;
            return Task.CompletedTask;
        }

        /// <summary>Simulates a payload that survives, for example an interrupted delete.</summary>
        public void RestorePayload(string secretReference, string secret) => References[secretReference] = secret;
    }

    /// <summary>A store that only implements the original three-member contract.</summary>
    private sealed class SaveOnlySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        private readonly bool _yieldOnSave;

        public SaveOnlySecretStore(bool yieldOnSave = false) => _yieldOnSave = yieldOnSave;

        public IReadOnlyCollection<string> References => _secrets.Keys;
        public Action<string>? BeforeDelete { get; set; }

        public async Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            // Yielding here releases the caller's continuation, so an overlapping save really does run
            // between minting a replacement and binding it. That is the window a pre-lock read of the
            // bound reference would sample from.
            if (_yieldOnSave)
            {
                await Task.Yield();
            }

            var reference = SecretReference.Create(Guid.NewGuid().ToString("N"));
            _secrets[reference] = secret;

            return reference;
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.TryGetValue(secretReference, out var secret) ? secret : null);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeDelete?.Invoke(secretReference);
            return Task.FromResult(_secrets.Remove(secretReference));
        }
    }

    private sealed class FakeSecretReferenceRepository : ISecretReferenceRepository
    {
        private readonly Dictionary<string, SecretReferenceMetadata> _metadata = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SecretReferenceOwnerBinding>> _owners = new(StringComparer.Ordinal);

        public bool FailOnInsert { get; set; }
        public bool FailOnMarkRotated { get; set; }
        public bool FailOnAddOwner { get; set; }
        public Func<Task>? BeforeStateUpdate { get; set; }
        public Func<Task>? AfterInsert { get; set; }
        public Func<Task>? AfterAddOwner { get; set; }

        public Task<SecretReferenceMetadata?> GetAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_metadata.TryGetValue(reference, out var metadata) ? metadata : null);
        }

        public Task<IReadOnlyList<SecretReferenceMetadata>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecretReferenceMetadata>>(_metadata.Values.ToList());

        public Task<IReadOnlyList<SecretReferenceOwnerBinding>> ListOwnersAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecretReferenceOwnerBinding>>(
                _owners.TryGetValue(reference, out var owners) ? owners.ToList() : new List<SecretReferenceOwnerBinding>());

        public async Task InsertAsync(SecretReferenceMetadata metadata, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOnInsert)
            {
                throw new InvalidOperationException("Simulated metadata failure.");
            }

            _metadata[metadata.Reference] = metadata;
            if (AfterInsert is not null) await AfterInsert();
        }

        public Task MarkRotatedAsync(string reference, DateTimeOffset lastRotatedAtUtc, CancellationToken cancellationToken = default)
        {
            if (FailOnMarkRotated)
            {
                throw new InvalidOperationException("Simulated rotation metadata failure.");
            }

            if (_metadata.TryGetValue(reference, out var metadata))
            {
                _metadata[reference] = metadata with { LastRotatedAtUtc = lastRotatedAtUtc };
            }

            return Task.CompletedTask;
        }

        public async Task UpdateStateAsync(string reference, SecretReferenceState state, DateTimeOffset? revokedAtUtc, CancellationToken cancellationToken = default)
        {
            if (BeforeStateUpdate is not null) await BeforeStateUpdate();
            if (_metadata.TryGetValue(reference, out var metadata))
            {
                _metadata[reference] = metadata with { State = state, RevokedAtUtc = revokedAtUtc };
            }

        }

        public async Task AddOwnerAsync(SecretReferenceOwnerBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOnAddOwner)
            {
                throw new InvalidOperationException("Simulated owner binding failure.");
            }

            if (!_owners.TryGetValue(binding.Reference, out var owners))
            {
                owners = new List<SecretReferenceOwnerBinding>();
                _owners[binding.Reference] = owners;
            }

            if (!owners.Contains(binding))
            {
                owners.Add(binding);
            }

            if (AfterAddOwner is not null) await AfterAddOwner();
        }

        public Task<bool> DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            _owners.Remove(reference);

            return Task.FromResult(_metadata.Remove(reference));
        }
    }

    private sealed class FakeProviderProfileRepository : IProviderProfileRepository
    {
        private readonly Dictionary<string, ProviderProfile> _profiles = new(StringComparer.Ordinal);
        public Dictionary<string, string?> SecretReferences { get; } = new(StringComparer.Ordinal);

        public bool FailOnUpsert { get; set; }
        public Action? OnUpsertFailure { get; set; }
        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.ToArray());

        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.GetValueOrDefault(id));

        public Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
        {
            if (FailOnUpsert)
            {
                OnUpsertFailure?.Invoke();
                throw new InvalidOperationException("Simulated profile persistence failure.");
            }

            SecretReferences[profile.Id] = apiKeySecretReference ?? SecretReferences.GetValueOrDefault(profile.Id);
            var previous = _profiles.GetValueOrDefault(profile.Id);
            _profiles[profile.Id] = new ProviderProfile(profile.Id, profile.DisplayName, profile.Backend,
                profile.BaseUrl, profile.ExecutablePath, profile.MaxDataClass, profile.IsEnabled,
                profile.GatewayNativeId, profile.CustomHeaders ?? previous?.CustomHeaders,
                (previous?.Revision ?? -1) + 1);

            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(SecretReferences.GetValueOrDefault(id));

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            SecretReferences.Remove(id);
            _profiles.Remove(id);

            return Task.FromResult(true);
        }
    }

    private sealed class FakeAccountRepository : IAccountRepository
    {
        public bool FailOnSave { get; set; }

        public Dictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal)
        {
            [AccountId] = new Account(
                AccountId,
                ProviderId,
                "Account 1",
                null,
                AuthState.Valid,
                0,
                true,
                HealthState.Healthy,
                null,
                null,
                1,
                null)
        };

        public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.GetValueOrDefault(id));

        public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(string providerProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Account>>(Accounts.Values.ToList());

        public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Account>>(Accounts.Values.ToList());

        public Task SaveAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (FailOnSave)
            {
                throw new InvalidOperationException("Simulated account persistence failure.");
            }

            Accounts[account.Id] = account;

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            Accounts.Remove(id);

            return Task.CompletedTask;
        }

        public Task UpdateAuthStateAsync(string id, AuthState authState, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateCooldownAsync(string id, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.GetValueOrDefault(accountId)?.SecretReference);

        public Task SetSecretReferenceAsync(string accountId, string? secretReference)
        {
            if (Accounts.TryGetValue(accountId, out var account))
            {
                Accounts[accountId] = account.WithSecretReference(secretReference);
            }

            return Task.CompletedTask;
        }
    }
}

using System.Runtime.Versioning;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class AccountSecretReviewTests
{
    private static ProviderProfile Profile() => new("provider", "Provider", BackendType.OpenCode,
        "https://provider.test", null, DataClassification.PrivateSource, true);
    private static Account Account(string id = "account", string? reference = null) => new(id, "provider", id, null,
        AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null, secretReference: reference);

    [Fact]
    public async Task DeletingLastAccountCredential_CommitsRetirementAndRecoverablePayloadCleanup()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        await profiles.UpsertAsync(Profile()); await accounts.SaveAsync(Account());
        using var lifecycle = new SecretLifecycleService(payloads, metadata, profiles, accounts);
        var saved = await lifecycle.SaveAccountSecretAsync("account", "synthetic-deleted-account");

        await accounts.DeleteAsync("account");

        Assert.Null(await accounts.GetByIdAsync("account"));
        Assert.Equal(SecretReferenceState.Revoked, (await metadata.GetAsync(saved.Reference))!.State);
        Assert.Equal(saved.Reference, Assert.Single(await profiles.ListPendingSecretDeletionsAsync()));
        Assert.Equal("synthetic-deleted-account", await payloads.GetSecretAsync(saved.Reference));
        using var recovered = new SecretLifecycleService(payloads, metadata, profiles, accounts);
        Assert.Equal(0, await recovered.RetryPendingSecretCleanupAsync());
        Assert.Null(await payloads.GetSecretAsync(saved.Reference));
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountDeletion_PreservesSharedOrUnknownOwnerCredential(bool unknownOwner)
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        await profiles.UpsertAsync(Profile()); await accounts.SaveAsync(Account());
        using var lifecycle = new SecretLifecycleService(payloads, metadata, profiles, accounts);
        var saved = await lifecycle.SaveAccountSecretAsync("account", "synthetic-retained-account");
        if (!unknownOwner) await accounts.SaveAsync(Account("sibling", saved.Reference));
        else
        {
            await using var connection = await db.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO SecretReferenceOwners (Reference,OwnerKind,OwnerId) VALUES ($ref,'Unknown','external')";
            command.Parameters.AddWithValue("$ref", saved.Reference);
            await command.ExecuteNonQueryAsync();
        }
        await accounts.DeleteAsync("account");
        Assert.Null(await accounts.GetByIdAsync("account"));
        Assert.True((await lifecycle.GetStatusAsync(saved.Reference)).IsUsable);
        Assert.Equal("synthetic-retained-account", await payloads.GetSecretAsync(saved.Reference));
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("account")]
    [InlineData("header")]
    [InlineData("unknown")]
    public async Task ExplicitAccountRevoke_RefusesSharedReferenceWithoutDamagingRemainingBinding(string sharing)
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        await profiles.UpsertAsync(Profile()); await accounts.SaveAsync(Account());
        using var lifecycle = new SecretLifecycleService(payloads, metadata, profiles, accounts);
        var saved = await lifecycle.SaveAccountSecretAsync("account", "synthetic-shared");
        if (sharing == "account") await accounts.SaveAsync(Account("sibling", saved.Reference));
        else
        {
            // Deliberately omit owner metadata for actual profile/header bindings.
            await using var connection = await db.Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$ref", saved.Reference);
            command.CommandText = sharing switch
            {
                "profile" => "UPDATE ProviderProfiles SET ApiKeySecretReference=$ref WHERE Id='provider'",
                "header" => "UPDATE ProviderProfiles SET CustomHeadersJson=json_array(json_object('Name','X-Token','SecretReference',$ref)) WHERE Id='provider'",
                _ => "INSERT INTO SecretReferenceOwners (Reference,OwnerKind,OwnerId) VALUES ($ref,'Unknown','external')"
            };
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.RevokeAccountSecretAsync("account"));

        Assert.True((await lifecycle.GetStatusAsync(saved.Reference)).IsUsable);
        Assert.Equal("synthetic-shared", await payloads.GetSecretAsync(saved.Reference));
        Assert.Equal(saved.Reference, await accounts.GetSecretReferenceAsync("account"));
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
    }

    [Fact]
    public async Task ExplicitAccountRevoke_StillRevokesItsOwnUnsharedCredential()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        await profiles.UpsertAsync(Profile()); await accounts.SaveAsync(Account());
        using var lifecycle = new SecretLifecycleService(payloads, new SqliteSecretReferenceRepository(db.Factory), profiles, accounts);
        var saved = await lifecycle.SaveAccountSecretAsync("account", "synthetic-private");
        await lifecycle.RevokeAccountSecretAsync("account");
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(saved.Reference)).State);
        Assert.Null(await payloads.GetSecretAsync(saved.Reference));
        Assert.Empty(await profiles.ListPendingSecretDeletionsAsync());
    }

    [Fact]
    public async Task SecondaryProductionRepository_RefusesAllAccountMutationsAndKeepsCredentialActive()
    {
        using var db = new TestDatabase(); await db.InitializeAsync();
        using var primary = HostBootstrapper.BuildHost(appDataDirectory: db.Root);
        Assert.True(primary.Services.GetRequiredService<IApplicationInstanceGuard>().IsPrimarySupervisor);
        var profiles = primary.Services.GetRequiredService<IProviderProfileRepository>();
        var accounts = primary.Services.GetRequiredService<IAccountRepository>();
        await profiles.UpsertAsync(Profile()); await accounts.SaveAsync(Account());
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(payloads,
            primary.Services.GetRequiredService<ISecretReferenceRepository>(), profiles, accounts);
        var saved = await lifecycle.SaveAccountSecretAsync("account", "synthetic-original");
        using var secondary = HostBootstrapper.BuildHost(appDataDirectory: db.Root);
        Assert.True(secondary.Services.GetRequiredService<IApplicationInstanceGuard>().IsViewOnly);
        var reader = secondary.Services.GetRequiredService<IAccountRepository>();
        Assert.Equal(saved.Reference, (await reader.GetByIdAsync("account"))!.SecretReference);
        var setup = secondary.Services.GetRequiredService<LLMWorkGUI.Application.Accounts.IAdaptationAccountConfigurationService>();
        var requested = new LLMWorkGUI.Application.Accounts.AdaptationAccountConfiguration("account", "provider", saved.Reference,
            SecretReferenceState.Active, true, null, null, false);
        Func<Task>[] writes =
        [
            () => reader.SaveAsync(Account()),
            () => reader.SaveAsync(Account("new-account")),
            () => reader.ReplaceSecretReferenceAsync("account", "provider", saved.Reference, SecretReference.Prefix + Guid.NewGuid().ToString("N")),
            () => setup.SaveKeyAsync(requested, "synthetic-secondary-refused"),
            () => setup.ConfigureProviderAsync(requested, "openai"),
            () => reader.UpdateAuthStateAsync("account", AuthState.Invalid),
            () => reader.UpdateCooldownAsync("account", DateTimeOffset.UtcNow.AddHours(1)),
            () => reader.DeleteAsync("account")
        ];
        foreach (var write in writes) await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(write);
        var retained = Assert.Single(await accounts.ListAllAsync());
        Assert.Equal(saved.Reference, retained.SecretReference);
        Assert.Equal(AuthState.Valid, retained.AuthState); Assert.Null(retained.CooldownUntil);
        Assert.True((await lifecycle.GetStatusAsync(saved.Reference)).IsUsable);
        Assert.Equal("synthetic-original", await payloads.GetSecretAsync(saved.Reference));
        Assert.Empty(await ((IProviderDeletionStore)profiles).ListPendingSecretDeletionsAsync());
    }
}

using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class SqliteAccountRepositoryTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task AccountRepository_SavesExplicitNullSecretAndGatewayIdentity()
    {
        await _database.InitializeAsync();
        var providers = new SqliteProviderProfileRepository(_database.Factory);
        await providers.UpsertAsync(new ProviderProfile("provider", "Provider", BackendType.OpenCode,
            "http://127.0.0.1:8000", null, DataClassification.PrivateSource, true));
        var repo = new SqliteAccountRepository(_database.Factory);
        Account Create(string? secret) => new("account", "provider", "Account", null, AuthState.Valid,
            1, true, HealthState.Healthy, null, null, 1, null, secretReference: secret, gatewayNativeId: "native-account");
        await repo.SaveAsync(Create("urn:llmworkgui:secret:fixture"));
        await repo.SaveAsync(Create(null));
        Assert.Null(await repo.GetSecretReferenceAsync("account"));
        Assert.Equal("native-account", (await repo.GetByIdAsync("account"))!.GatewayNativeId);
    }

    [Fact]
    public async Task AccountRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);

        var provider = new ProviderProfile(
            "prov-test",
            "Provider Test",
            BackendType.OpenCode,
            "http://127.0.0.1:8080/v1",
            null,
            DataClassification.PrivateSource,
            isEnabled: true);

        await providerRepo.UpsertAsync(provider);

        var now = DateTimeOffset.UtcNow;
        var account = new Account(
            "acc-101",
            "prov-test",
            "Primary Developer Account",
            "native-user-1",
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 2,
            reserveThreshold: 0.1,
            secretReference: "urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001");

        // 1. Save
        await accountRepo.SaveAsync(account);

        // 2. GetById
        var fetched = await accountRepo.GetByIdAsync("acc-101");
        Assert.NotNull(fetched);
        Assert.Equal("acc-101", fetched!.Id);
        Assert.Equal("prov-test", fetched.ProviderProfileId);
        Assert.Equal("Primary Developer Account", fetched.DisplayName);
        Assert.Equal("native-user-1", fetched.ProviderNativeId);
        Assert.Equal(AuthState.Valid, fetched.AuthState);
        Assert.Equal(10, fetched.ManualPriority);
        Assert.True(fetched.IsEnabled);
        Assert.Equal(HealthState.Healthy, fetched.Health);
        Assert.Equal(2, fetched.MaxConcurrentExecutions);
        Assert.Equal(0.1, fetched.ReserveThreshold);
        Assert.Equal("urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001", fetched.SecretReference);

        // 3. Secret reference query
        var secretRef = await accountRepo.GetSecretReferenceAsync("acc-101");
        Assert.Equal("urn:llmworkgui:secret:00000000-0000-0000-0000-000000000001", secretRef);

        // 4. Update AuthState
        await accountRepo.UpdateAuthStateAsync("acc-101", AuthState.Invalid);
        var afterAuthUpdate = await accountRepo.GetByIdAsync("acc-101");
        Assert.NotNull(afterAuthUpdate);
        Assert.Equal(AuthState.Invalid, afterAuthUpdate!.AuthState);

        // 5. Update Cooldown
        var cooldownTime = now.AddMinutes(15);
        await accountRepo.UpdateCooldownAsync("acc-101", cooldownTime);
        var afterCooldownUpdate = await accountRepo.GetByIdAsync("acc-101");
        Assert.NotNull(afterCooldownUpdate);
        Assert.NotNull(afterCooldownUpdate!.CooldownUntil);

        // 6. ListByProviderProfileId
        var secondAccount = new Account(
            "acc-102",
            "prov-test",
            "Secondary Fallback Account",
            null,
            AuthState.Valid,
            20, // Higher priority
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            null);
        await accountRepo.SaveAsync(secondAccount);

        var list = await accountRepo.ListByProviderProfileIdAsync("prov-test");
        Assert.Equal(2, list.Count);
        // Ordered by ManualPriority DESC -> acc-102 (20) before acc-101 (10)
        Assert.Equal("acc-102", list[0].Id);
        Assert.Equal("acc-101", list[1].Id);

        // 7. Delete
        await accountRepo.DeleteAsync("acc-101");
        var afterDelete = await accountRepo.GetByIdAsync("acc-101");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task AccountRepository_CascadeDeletes_WhenProviderProfileDeleted()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);

        var provider = new ProviderProfile(
            "prov-cascade",
            "Provider Cascade",
            BackendType.OpenCode,
            null,
            null,
            DataClassification.PrivateSource,
            isEnabled: true);

        await providerRepo.UpsertAsync(provider);

        var account = new Account(
            "acc-cascade",
            "prov-cascade",
            "Account Cascade",
            null,
            AuthState.Valid,
            0,
            true,
            HealthState.Healthy,
            null,
            null,
            1,
            null);

        await accountRepo.SaveAsync(account);

        Assert.NotNull(await accountRepo.GetByIdAsync("acc-cascade"));

        // Delete Provider Profile
        await providerRepo.DeleteAsync("prov-cascade");

        // Verify account is cascade deleted
        var afterProviderDelete = await accountRepo.GetByIdAsync("acc-cascade");
        Assert.Null(afterProviderDelete);
    }
}

using System.Runtime.Versioning;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class ProviderCredentialAccountBindingTests
{
    [Fact]
    public async Task AccountCredentialWorksOnlyForItsConfiguredAccountAndProvider()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        await db.SeedRouteChainAsync();
        var profiles = new SqliteProviderProfileRepository(db.Factory);
        var accounts = new SqliteAccountRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), profiles, accounts);
        var credential = await lifecycle.SaveAccountSecretAsync("account-1", "synthetic-account-key");
        await using var server = new LocalMockAiServer { ExpectedApiKey = "synthetic-account-key" };
        await server.StartAsync();
        var get = new ProviderConnectionTestService(store, secretLifecycle: lifecycle, profiles: profiles, accounts: accounts);
        Assert.True((await get.TestConnectionAsync(new("provider-1", "Provider", server.BaseUrl, credential.Reference,
            accountId: "account-1"))).IsSuccessful);
        var post = new ProviderModelProbeExecutor(store, secretLifecycle: lifecycle, profiles: profiles, accounts: accounts);
        var request = new ModelProbeRequest
        {
            Scope = HealthScope.ForAccount("account-1"), ProviderProfileId = "provider-1", AccountId = "account-1",
            ModelId = "mock-gpt-4o", BaseUrl = server.BaseUrl, ApiKeySecretReference = credential.Reference
        };
        Assert.Equal(ModelProbeOutcome.Succeeded, (await post.ExecuteAsync(request)).Outcome);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(ModelProbeOutcome.CredentialUnavailable,
            (await post.ExecuteAsync(request with { AccountId = "other-account" })).Outcome);
        Assert.Equal(ModelProbeOutcome.CredentialUnavailable,
            (await post.ExecuteAsync(request with { ProviderProfileId = "other-provider" })).Outcome);
        Assert.Equal(2, server.RequestCount);
    }
}

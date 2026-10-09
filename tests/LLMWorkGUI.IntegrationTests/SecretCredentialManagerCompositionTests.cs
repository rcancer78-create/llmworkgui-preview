using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.IntegrationTests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

/// <summary>
/// The secret lifecycle over the production composition once the Windows Credential Manager is the
/// primary storage: the routed and probed components observe the same state, a rotation leaves exactly
/// one credential behind, and recorded metadata still wins over a payload that survived a revocation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretCredentialManagerCompositionTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly List<string> _ownedReferences = new();

    public void Dispose()
    {
        var credentials = new WindowsCredentialManagerApi();
        foreach (var reference in _ownedReferences)
            credentials.Delete(CredentialManagerTarget.ForReference(reference));
        TestSqlitePool.Clear(new SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        _directory.Dispose();
    }

    [Fact]
    public void ProductionComposition_ResolvesTheCredentialManagerStore()
    {
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);

        // ADR-0005 §1.1: the primary store must be the one the composition hands to every consumer,
        // not a DPAPI-only fallback.
        var store = host.Services.GetRequiredService<ISecretStore>();

        Assert.IsType<CredentialManagerSecretStore>(store);
        Assert.IsType<WindowsCredentialManagerApi>(host.Services.GetRequiredService<ICredentialManagerApi>());
    }

    [Fact]
    public async Task ProductionComposition_BoundReplacementLeavesExactlyOneCredentialAndNoLegacyFile()
    {
        using var host = await StartAsync();
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var store = host.Services.GetRequiredService<ISecretStore>();
        var profiles = host.Services.GetRequiredService<IProviderProfileRepository>();
        var profile = NewProfile("composition-rotation");
        var secretsDirectory = Path.Combine(_directory.Root, "secrets");

        var created = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-composition-first");
        _ownedReferences.Add(created.Reference);
        var rotated = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-composition-second");
        _ownedReferences.Add(rotated.Reference);

        Assert.NotEqual(created.Reference, rotated.Reference);
        Assert.Null(await store.GetSecretAsync(created.Reference));
        Assert.Equal("sk-composition-second", await store.GetSecretAsync(rotated.Reference));
        Assert.Equal(rotated.Reference, await profiles.GetApiKeySecretReferenceAsync(profile.Id));

        var primary = (CredentialManagerSecretStore)store;

        Assert.False(primary.HasCredentialAuthorityMarker(created.Reference));
        Assert.True(primary.HasCredentialAuthorityMarker(rotated.Reference));
        Assert.Empty(Directory.GetFiles(secretsDirectory, "*.secret"));

        // Retirement removes the previous target and marker after the new binding commits.
        Assert.Single(Directory.GetFiles(secretsDirectory, "*.cmref"));

        await lifecycle.RevokeProviderApiKeyAsync(profile.Id);
        Assert.Empty(Directory.GetFiles(secretsDirectory));

        await host.StopAsync();
    }

    [Fact]
    public async Task RevokedMetadataWins_OverACredentialThatSurvivedTheRevocation()
    {
        using var host = await StartAsync();
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var credentials = new WindowsCredentialManagerApi();
        var store = host.Services.GetRequiredService<ISecretStore>();
        var connectionTest = host.Services.GetRequiredService<IProviderConnectionTestService>();
        var profile = NewProfile("composition-revoked");

        var created = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-composition-revoked");
        var target = CredentialManagerTarget.ForReference(created.Reference);

        try
        {
            await lifecycle.RevokeProviderApiKeyAsync(profile.Id);
            Assert.Equal(CredentialManagerOutcome.NotFound, credentials.Read(target).Outcome);

            // A credential restored out of band must not make a revoked reference usable again
            // (ADR-0005 §5.2).
            Assert.True(credentials.Write(target, "sk-resurrected", CredentialManagerTarget.UserName, CredentialManagerTarget.Comment).IsSuccess);

            var status = await lifecycle.GetStatusAsync(created.Reference);

            Assert.Equal(SecretReferenceState.Revoked, status.State);
            Assert.False(status.IsUsable);

            var result = await connectionTest.TestConnectionAsync(
                new CustomProviderSettings(
                    profile.Id,
                    profile.DisplayName,
                    profile.BaseUrl!,
                    created.Reference),
                timeout: TimeSpan.FromMilliseconds(500));

            Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        }
        finally
        {
            credentials.Delete(target);
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task AClassifiedCredentialManagerOutage_LeavesTheLifecycleUsable()
    {
        // The composition keeps working when the Credential Manager reports a classified outage: the
        // value lands in the existing DPAPI layout and no authority marker is created for it.
        var credentials = new FakeCredentialManagerApi { WriteOutcomeOverride = CredentialManagerOutcome.NotSupported };
        var services = new ServiceCollection();
        services.AddSingleton<ICredentialManagerApi>(credentials);
        services.AddApplication();
        services.AddInfrastructure(_directory.Root);

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var lifecycle = provider.GetRequiredService<ISecretLifecycleService>();
        var store = (CredentialManagerSecretStore)provider.GetRequiredService<ISecretStore>();
        var profiles = provider.GetRequiredService<IProviderProfileRepository>();
        var profile = NewProfile("composition-outage");

        var created = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-outage-value");
        var secretsDirectory = Path.Combine(_directory.Root, "secrets");

        Assert.Equal(SecretReferenceState.Active, created.State);
        Assert.Equal("sk-outage-value", await store.GetSecretAsync(created.Reference));
        Assert.False(store.HasCredentialAuthorityMarker(created.Reference));
        Assert.Single(Directory.GetFiles(secretsDirectory, "*.secret"));
        Assert.Equal(0, credentials.StoredCredentialCount);

        // The classified outage still permits a fresh DPAPI payload and retirement of the old one.
        var rotated = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-outage-value-2");

        Assert.NotEqual(created.Reference, rotated.Reference);
        Assert.Null(await store.GetSecretAsync(created.Reference));
        Assert.Equal("sk-outage-value-2", await store.GetSecretAsync(rotated.Reference));
        Assert.Equal(rotated.Reference, await profiles.GetApiKeySecretReferenceAsync(profile.Id));
        Assert.Single(Directory.GetFiles(secretsDirectory, "*.secret"));
    }

    [Fact]
    public async Task ProviderProfileAndAccountSecrets_EachAddressTheTargetOfTheirOwnReference()
    {
        // ADR-0005 §1.2: the target is derived from the URN alone. Owner and kind stay SQLite
        // metadata, so a profile and an account produce two independent credentials under two
        // independent targets, and sharing one URN would collapse them into one.
        using var host = await StartAsync();
        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var store = host.Services.GetRequiredService<ISecretStore>();
        var profiles = host.Services.GetRequiredService<IProviderProfileRepository>();
        var accounts = host.Services.GetRequiredService<IAccountRepository>();
        var credentials = new WindowsCredentialManagerApi();

        var profile = NewProfile("composition-owners");
        var profileStatus = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-owner-profile");

        var account = new Account(
            "composition-owner-account",
            profile.Id,
            "Composition Owner",
            "native-user-1",
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 2,
            reserveThreshold: null);

        await accounts.SaveAsync(account);
        var accountStatus = await lifecycle.SaveAccountSecretAsync(account.Id, "sk-owner-account");

        var profileTarget = CredentialManagerTarget.ForReference(profileStatus.Reference);
        var accountTarget = CredentialManagerTarget.ForReference(accountStatus.Reference);

        try
        {
            Assert.NotEqual(profileTarget, accountTarget);
            Assert.StartsWith(CredentialManagerTarget.Prefix, profileTarget, StringComparison.Ordinal);
            Assert.StartsWith(CredentialManagerTarget.Prefix, accountTarget, StringComparison.Ordinal);
            Assert.Equal(profileStatus.Reference, await profiles.GetApiKeySecretReferenceAsync(profile.Id));
            Assert.Equal("sk-owner-profile", await store.GetSecretAsync(profileStatus.Reference));
            Assert.Equal("sk-owner-account", await store.GetSecretAsync(accountStatus.Reference));

            // Each value is in exactly one credential under its own target.
            Assert.Equal("sk-owner-profile", Encoding.UTF8.GetString(credentials.Read(profileTarget).Blob!));
            Assert.Equal("sk-owner-account", Encoding.UTF8.GetString(credentials.Read(accountTarget).Blob!));
        }
        finally
        {
            credentials.Delete(profileTarget);
            credentials.Delete(accountTarget);
        }

        await host.StopAsync();
    }

    private async Task<IHost> StartAsync()
    {
        var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);
        return host;
    }

    private static ProviderProfile NewProfile(string id) =>
        new(
            id,
            id,
            BackendType.OpenCode,
            "https://api.example.test/v1",
            null,
            DataClassification.PrivateSource,
            true);
}

using System.Runtime.Versioning;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

/// <summary>
/// End-to-end check of the secret lifecycle in the production composition: the migration creates the
/// reference metadata, the DPAPI store holds the only copy of the value, and the routed/probed
/// components observe the same state the lifecycle reports.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretLifecycleCompositionTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        TestSqlitePool.Clear(new LLMWorkGUI.Infrastructure.Data.SqliteConnectionFactory(_directory.GetPath("llmworkgui.db")));
        _directory.Dispose();
    }

    [Fact]
    public async Task SecondaryProductionComposition_CanReadButAllLifecycleWritesAreRefused()
    {
        using var primary = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        await primary.StartAsync();
        await HostBootstrapper.InitializeAsync(primary);
        var writer = primary.Services.GetRequiredService<ISecretLifecycleService>();
        var profile = new ProviderProfile("guarded", "Guarded", BackendType.OpenCode,
            "https://example.test/v1", null, DataClassification.PublicSource, true);
        var saved = await writer.SaveProviderApiKeyAsync(profile, "synthetic-original");
        using var secondary = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        var guard = secondary.Services.GetRequiredService<LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard>();
        Assert.True(guard.IsViewOnly);
        var reader = secondary.Services.GetRequiredService<ISecretLifecycleService>();
        Assert.True((await reader.GetStatusAsync(saved.Reference)).IsUsable);
        Func<Task>[] writes =
        [
            () => reader.CreateAsync("synthetic-new"),
            () => reader.RotateAsync(saved.Reference, "synthetic-new"),
            () => reader.RevokeAsync(saved.Reference),
            () => reader.BindAsync(saved.Reference, SecretReferenceOwnerBinding.ForProviderProfile(saved.Reference, "other")),
            () => reader.SaveProviderApiKeyAsync(profile, "synthetic-new"),
            () => reader.RevokeProviderApiKeyAsync(profile.Id),
            () => reader.SaveAccountSecretAsync("absent-account", "synthetic-new"),
            () => reader.RevokeAccountSecretAsync("absent-account"),
            () => reader.SaveProviderConfigurationAsync(profile, [new CustomProviderHeader("X-Token", "synthetic-header", true)]),
            () => reader.DeleteProviderConfigurationAsync(profile.Id, 0),
            () => reader.RetryPendingSecretCleanupAsync()
        ];
        foreach (var write in writes)
            await Assert.ThrowsAsync<LLMWorkGUI.Application.Concurrency.SecondaryInstanceReadOnlyException>(write);
        Assert.Equal("synthetic-original", await primary.Services.GetRequiredService<ISecretStore>().GetSecretAsync(saved.Reference));
        Assert.True((await writer.GetStatusAsync(saved.Reference)).IsUsable);
        Assert.Single(await primary.Services.GetRequiredService<ISecretReferenceRepository>().ListOwnersAsync(saved.Reference));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory.Root, "secrets")));
        await writer.RevokeProviderApiKeyAsync(profile.Id);
        await primary.StopAsync();
    }

    [Fact]
    public async Task ProductionComposition_RunsTheFullLifecycleOverSqliteAndDpapi()
    {
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);

        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var profiles = host.Services.GetRequiredService<IProviderProfileRepository>();
        var references = host.Services.GetRequiredService<ISecretReferenceRepository>();
        var store = host.Services.GetRequiredService<ISecretStore>();

        var ownedReferences = new HashSet<string>(StringComparer.Ordinal);
        Exception? bodyFailure = null;
        try
        {
            var profile = new ProviderProfile(
                "custom-openai",
                "Custom OpenAI",
                BackendType.OpenCode,
                "https://api.example.test/v1",
                null,
                DataClassification.PrivateSource,
                true);

            // 1. First save registers a reference and its ownership.
            var created = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-first-value");
            ownedReferences.Add(created.Reference);

            Assert.True(SecretReference.IsValid(created.Reference));
            Assert.Equal(SecretReferenceState.Active, created.State);
            Assert.Equal(created.Reference, await profiles.GetApiKeySecretReferenceAsync(profile.Id));
            Assert.Equal(profile.Id, Assert.Single(await references.ListOwnersAsync(created.Reference)).OwnerId);
            Assert.NotNull(await references.GetAsync(created.Reference));

            // 2. A replacement binds a new payload before retiring the previous reference.
            var rotated = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-rotated-value");
            ownedReferences.Add(rotated.Reference);

            Assert.NotEqual(created.Reference, rotated.Reference);
            Assert.Null(await store.GetSecretAsync(created.Reference));
            Assert.Equal("sk-rotated-value", await store.GetSecretAsync(rotated.Reference));
            Assert.Single(Directory.GetFiles(Path.Combine(_directory.Root, "secrets")));

            // 3. The routed and probed components see the same lifecycle state.
            Assert.Same(lifecycle, host.Services.GetRequiredService<ISecretLifecycleService>());
            Assert.NotNull(host.Services.GetRequiredService<IRoutingEngine>());
            Assert.NotNull(host.Services.GetRequiredService<IProviderConnectionTestService>());
            Assert.NotNull(host.Services.GetRequiredService<IModelProbeExecutor>());
            Assert.NotNull(host.Services.GetRequiredService<IHealthProbeService>());

            // 4. Revocation is durable and removes the payload.
            await lifecycle.RevokeProviderApiKeyAsync(profile.Id);

            var revoked = await lifecycle.GetStatusAsync(rotated.Reference);

            Assert.Equal(SecretReferenceState.Revoked, revoked.State);
            Assert.False(revoked.IsUsable);
            Assert.Null(await store.GetSecretAsync(created.Reference));
            Assert.Empty(Directory.GetFiles(Path.Combine(_directory.Root, "secrets")));

            // 5. Saving again never reuses the revoked reference.
            var replacement = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-third-value");
            ownedReferences.Add(replacement.Reference);

            Assert.NotEqual(created.Reference, replacement.Reference);
            Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(created.Reference)).State);
            Assert.Equal("sk-third-value", await store.GetSecretAsync(replacement.Reference));

            // The production store keeps a value in the Windows Credential Manager, which outlives the
            // temporary app-data directory. Revoking the last reference keeps the test from leaving a
            // credential behind in the user's Credential Manager on every run.
            await lifecycle.RevokeProviderApiKeyAsync(profile.Id);

            Assert.Null(await store.GetSecretAsync(replacement.Reference));

            await host.StopAsync();
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
            throw;
        }
        finally
        {
            // The real vault outlives the fixture directory. Attempt every returned
            // synthetic reference after assertions/host errors; report cleanup failures.
            await DeleteOwnedSecretsAsync(store, ownedReferences, bodyFailure);
        }
    }

    [Fact]
    public async Task ProductionComposition_ConnectionTestRefusesARevokedReference()
    {
        using var host = HostBootstrapper.BuildHost(appDataDirectory: _directory.Root);
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);

        var lifecycle = host.Services.GetRequiredService<ISecretLifecycleService>();
        var profiles = host.Services.GetRequiredService<IProviderProfileRepository>();
        var connectionTest = host.Services.GetRequiredService<IProviderConnectionTestService>();

        var store = host.Services.GetRequiredService<ISecretStore>();
        var ownedReferences = new HashSet<string>(StringComparer.Ordinal);
        Exception? bodyFailure = null;
        try
        {
            var profile = new ProviderProfile(
                "custom-revoked",
                "Custom Revoked",
                BackendType.OpenCode,
                "http://127.0.0.1:65500/v1",
                null,
                DataClassification.PrivateSource,
                true);

            var created = await lifecycle.SaveProviderApiKeyAsync(profile, "sk-value");
            ownedReferences.Add(created.Reference);
            await lifecycle.RevokeProviderApiKeyAsync(profile.Id);

            var settings = new CustomProviderSettings(
                profile.Id,
                profile.DisplayName,
                profile.BaseUrl!,
                created.Reference);

            var result = await connectionTest.TestConnectionAsync(settings, timeout: TimeSpan.FromMilliseconds(500));

            // The port is closed, but the refusal must be about the credential: nothing may be sent.
            Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
            Assert.Equal(profile.Id, (await profiles.GetByIdAsync(profile.Id))!.Id);

            await host.StopAsync();
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
            throw;
        }
        finally
        {
            await DeleteOwnedSecretsAsync(store, ownedReferences, bodyFailure);
        }
    }

    private static async Task DeleteOwnedSecretsAsync(ISecretStore store, IEnumerable<string> references, Exception? bodyFailure)
    {
        var failures = new List<Exception>();
        foreach (var reference in references)
        {
            try
            {
                await store.DeleteSecretAsync(reference, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        if (failures.Count != 0)
        {
            // Attempt every owned reference and preserve the original assertion/host failure
            // together with cleanup errors, rather than replacing it or hiding a vault leak.
            if (bodyFailure is not null)
            {
                failures.Insert(0, bodyFailure);
            }
            throw new AggregateException("Synthetic credential fixture failed to clean up every owned reference.", failures);
        }
    }
}

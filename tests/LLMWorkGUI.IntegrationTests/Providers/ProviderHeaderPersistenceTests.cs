using System.Net;
using System.Runtime.Versioning;
using System.Text;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

[SupportedOSPlatform("windows")]
public sealed class ProviderHeaderPersistenceTests
{
    private const string Canary = "synthetic-header-plaintext-canary-8675309";
    private static ProviderProfile Profile(string id = "provider", IReadOnlyList<ProviderHeader>? headers = null) =>
        new(id, "Provider", BackendType.OpenCode, "https://provider.test/v1", null,
            DataClassification.PrivateSource, true, customHeaders: headers);

    [Fact]
    public async Task Restart_ResolvesOnlyAtSend_PreservesPublicHeaders_AndNeverPersistsPlaintextSecret()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        var directory = Path.Combine(db.Root, "secrets");
        var store = new DpapiSecretStore(directory);
        using (var writer = new SecretLifecycleService(store, metadata, repo))
        {
            await writer.SaveProviderConfigurationAsync(Profile(),
                [new("X-Region", "east"), new("X-Token", Canary), new("X-Private", Canary + "-second", true)], "api-canary");
        }
        // Simulate an unrelated profile edit that does not own the header fields.
        await repo.UpsertAsync(Profile());
        var restartedRepo = new SqliteProviderProfileRepository(db.Factory);
        var restartedStore = new DpapiSecretStore(directory);
        using var reader = new SecretLifecycleService(restartedStore, new SqliteSecretReferenceRepository(db.Factory), restartedRepo);
        var loaded = (await restartedRepo.GetByIdAsync("provider"))!;
        Assert.Equal(3, loaded.CustomHeaders!.Count);
        Assert.Equal("east", loaded.CustomHeaders[0].Value);
        Assert.All(loaded.CustomHeaders.Skip(1), h => Assert.Null(h.Value));
        var headers = loaded.CustomHeaders.Select(h => new CustomProviderHeader(h.Name, h.Value ?? "", secretReference: h.SecretReference)).ToArray();
        var settings = new CustomProviderSettings("provider", "Provider", loaded.BaseUrl!,
            await restartedRepo.GetApiKeySecretReferenceAsync("provider"), headers);
        var preview = new OpenCodeConfigService().GeneratePreview(settings);
        Assert.DoesNotContain(Canary, preview.ToString());
        using var handler = new RecordingHandler(request =>
        {
            Assert.Equal("east", Assert.Single(request.Headers.GetValues("X-Region")));
            Assert.Equal(Canary, Assert.Single(request.Headers.GetValues("X-Token")));
            Assert.Equal(Canary + "-second", Assert.Single(request.Headers.GetValues("X-Private")));
            Assert.Equal("api-canary", request.Headers.Authorization!.Parameter);
        });
        using var client = new HttpClient(handler);
        Assert.True((await new ProviderConnectionTestService(restartedStore, client, reader, restartedRepo).TestConnectionAsync(settings)).IsSuccessful);
        var probe = await new ProviderModelProbeExecutor(restartedStore, client, reader, restartedRepo).ExecuteAsync(new ModelProbeRequest
        {
            Scope = HealthScope.ForModelRoute("account", "model"), ProviderProfileId = "provider", ModelId = "model",
            BaseUrl = settings.BaseUrl, ApiKeySecretReference = settings.ApiKeySecretRef, CustomHeaders = headers
        });
        Assert.Equal(ModelProbeOutcome.Succeeded, probe.Outcome);
        Assert.Equal(2, handler.Count);
        foreach (var file in Directory.EnumerateFiles(db.Root, "*", SearchOption.AllDirectories))
        {
            // Inspect DB, WAL, and encrypted files, not just a returned DTO.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }

    [Fact]
    public async Task FailedCombinedSave_PreservesKeyAndOldHeader_CompensatesNewPayloads_ThenRetryReplacesBoth()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        using var lifecycle = new SecretLifecycleService(store, metadata, repo);
        var original = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", "old-header")], "old-key");
        var oldRef = Assert.Single(original.Profile.CustomHeaders!).SecretReference!;
        var keyRef = (await repo.GetApiKeySecretReferenceAsync("provider"))!;
        await ExecuteAsync(db, "CREATE TRIGGER fail_header_save BEFORE UPDATE ON ProviderProfiles BEGIN SELECT RAISE(ABORT,'synthetic fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", Canary)], "new-key"));
        Assert.Equal(oldRef, Assert.Single((await repo.GetByIdAsync("provider"))!.CustomHeaders!).SecretReference);
        Assert.Equal("old-header", await store.GetSecretAsync(oldRef));
        Assert.Equal("old-key", await store.GetSecretAsync(keyRef));
        var failedRefs = (await metadata.ListAsync()).Where(m => m.Reference != oldRef && m.Reference != keyRef).ToArray();
        Assert.Equal(2, failedRefs.Length);
        foreach (var failedRef in failedRefs)
        {
            Assert.Equal(SecretReferenceState.Revoked, failedRef.State);
            Assert.Null(await store.GetSecretAsync(failedRef.Reference));
        }
        await ExecuteAsync(db, "DROP TRIGGER fail_header_save;");
        var replacement = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", Canary)], "new-key");
        Assert.Equal(Canary, await store.GetSecretAsync(Assert.Single(replacement.Profile.CustomHeaders!).SecretReference!));
        Assert.NotEqual(keyRef, replacement.ApiKeySecretReference);
        Assert.Null(await store.GetSecretAsync(keyRef));
        Assert.Equal("new-key", await store.GetSecretAsync(replacement.ApiKeySecretReference!));
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(oldRef)).State);
        Assert.Null(await store.GetSecretAsync(oldRef));
        await lifecycle.SaveProviderConfigurationAsync(Profile(), []);
        Assert.Empty((await repo.GetByIdAsync("provider"))!.CustomHeaders!);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(replacement.Profile.CustomHeaders![0].SecretReference!)).State);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("missing")]
    [InlineData("wrong-purpose")]
    [InlineData("foreign-provider")]
    [InlineData("foreign-name")]
    public async Task UnusableOrForeignReference_SendsNoRequest(string failure)
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", Canary)]);
        var reference = saved.Profile.CustomHeaders![0].SecretReference!;
        if (failure == "revoked") await lifecycle.RevokeAsync(reference);
        if (failure == "missing") await store.DeleteSecretAsync(reference);
        if (failure == "wrong-purpose") await ExecuteAsync(db, "UPDATE SecretReferences SET Kind='GatewayApiKey';");
        var headers = new[] { new CustomProviderHeader(failure == "foreign-name" ? "X-Other" : "X-Token", "", secretReference: reference) };
        var providerId = failure == "foreign-provider" ? "other" : "provider";
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var service = new ProviderConnectionTestService(store, client, lifecycle, repo);
        var result = await service.TestConnectionAsync(new(providerId, "Provider", saved.Profile.BaseUrl!, customHeaders: headers));
        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SaveProviderConfigurationAsync(Profile(providerId), headers));
        var probe = await new ProviderModelProbeExecutor(store, client, lifecycle, repo).ExecuteAsync(new ModelProbeRequest
        {
            Scope = HealthScope.ForModelRoute("account", "model"), ProviderProfileId = providerId,
            ModelId = "model", BaseUrl = saved.Profile.BaseUrl, CustomHeaders = headers
        });
        Assert.Equal(ModelProbeOutcome.CredentialUnavailable, probe.Outcome);
        Assert.Null(probe.FailureClass);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task DuplicateHeadersAndPlaintextSensitiveStorage_AreRejectedBeforeSideEffects()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var metadata = new SqliteSecretReferenceRepository(db.Factory);
        using var lifecycle = new SecretLifecycleService(store, metadata, repo);
        await Assert.ThrowsAsync<ArgumentException>(() => lifecycle.SaveProviderConfigurationAsync(Profile(),
            [new("X-Token", Canary), new("x-token", "duplicate")]));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(Profile(headers: [new("Authorization", Canary)])));
        Assert.Empty(await metadata.ListAsync());
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task DeleteCredentialOperation_RevokesHeadersEvenWithoutApiKey()
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repo = new SqliteProviderProfileRepository(db.Factory);
        var store = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(db.Factory), repo);
        var saved = await lifecycle.SaveProviderConfigurationAsync(Profile(), [new("X-Token", Canary)]);
        var reference = saved.Profile.CustomHeaders![0].SecretReference!;
        await lifecycle.RevokeProviderApiKeyAsync("provider");
        Assert.True((await lifecycle.GetStatusAsync(reference)).IsUsable); // API-key-only revocation leaves headers alone.
        await lifecycle.DeleteProviderConfigurationAsync("provider", saved.Profile.Revision!.Value);
        Assert.Equal(SecretReferenceState.Revoked, (await lifecycle.GetStatusAsync(reference)).State);
        Assert.Null(await store.GetSecretAsync(reference));
    }

    private static async Task ExecuteAsync(TestDatabase db, string sql)
    {
        await using var connection = await db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingHandler(Action<HttpRequestMessage> verify) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            verify(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get
                    ? "{\"data\":[{\"id\":\"model\"}]}"
                    : "{\"model\":\"model\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")
            });
        }
    }
}

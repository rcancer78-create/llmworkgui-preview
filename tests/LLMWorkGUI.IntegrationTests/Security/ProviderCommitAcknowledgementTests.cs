using System.Runtime.Versioning;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

[SupportedOSPlatform("windows")]
public sealed class ProviderCommitAcknowledgementTests
{
    [Theory]
    [InlineData("key", "io")]
    [InlineData("key", "cancel")]
    [InlineData("key", "unknown")]
    [InlineData("headers", "io")]
    [InlineData("headers", "cancel")]
    [InlineData("headers", "unknown")]
    [InlineData("both", "io")]
    [InlineData("both", "cancel")]
    [InlineData("both", "unknown")]
    public async Task CommittedProviderCredentialsSurviveLostAcknowledgement(string operation, string failure)
    {
        using var db = new TestDatabase();
        await db.InitializeAsync();
        var repository = new SqliteProviderProfileRepository(db.Factory);
        var payloads = new DpapiSecretStore(Path.Combine(db.Root, "secrets"));
        var references = new SqliteSecretReferenceRepository(db.Factory);
        var original = new ProviderProfile("provider", "Provider", BackendType.OpenCode,
            "https://example.test", null, DataClassification.PrivateSource, true);
        using var initial = new SecretLifecycleService(payloads, references, repository);
        var saved = await initial.SaveProviderConfigurationAsync(original,
            [new CustomProviderHeader("X-Token", "synthetic-old-header", true)], "synthetic-old-key");
        using var cancellation = new CancellationTokenSource();
        var uncertain = new LostAcknowledgementRepository(repository, failure, cancellation);
        using var lifecycle = new SecretLifecycleService(payloads, references, uncertain);

        var error = await Record.ExceptionAsync(async () =>
        {
            if (operation == "key")
                await lifecycle.SaveProviderApiKeyAsync(saved.Profile, "synthetic-new-key", cancellation.Token);
            else
                await lifecycle.SaveProviderConfigurationAsync(saved.Profile,
                    [new CustomProviderHeader("X-Token", "synthetic-new-header", true)],
                    operation == "both" ? "synthetic-new-key" : null, cancellation.Token);
        });

        // Inspect the real committed owner independently from its broken acknowledgement wrapper.
        var committed = Assert.IsType<ProviderProfile>(await repository.GetByIdAsync("provider"));
        var key = await repository.GetApiKeySecretReferenceAsync("provider");
        var header = Assert.Single(committed.CustomHeaders!).SecretReference!;
        Assert.Equal(operation == "key" ? "synthetic-old-header" : "synthetic-new-header",
            await payloads.GetSecretAsync(header));
        Assert.True((await lifecycle.GetStatusAsync(header)).IsUsable);
        Assert.Equal(operation == "headers" ? "synthetic-old-key" : "synthetic-new-key",
            await payloads.GetSecretAsync(key!));
        Assert.True((await lifecycle.GetStatusAsync(key!)).IsUsable);
        if (failure == "unknown") Assert.NotNull(error);
        else Assert.Null(error);
    }

    private sealed class LostAcknowledgementRepository(IProviderProfileRepository inner, string failure,
        CancellationTokenSource cancellation) : IProviderProfileRepository
    {
        private bool _committed;
        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken token = default) => inner.ListAsync(token);
        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken token = default) =>
            _committed && failure == "unknown" ? throw new IOException("synthetic owner reread unavailable") : inner.GetByIdAsync(id, token);
        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken token = default) =>
            _committed && failure == "unknown" ? throw new IOException("synthetic owner reread unavailable") : inner.GetApiKeySecretReferenceAsync(id, token);
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id, token);
        public async Task UpsertAsync(ProviderProfile profile, string? reference = null, CancellationToken token = default) =>
            _ = await UpsertReturningRevisionAsync(profile, reference, token);
        public async Task<long> UpsertReturningRevisionAsync(ProviderProfile profile, string? reference = null,
            CancellationToken token = default)
        {
            await inner.UpsertReturningRevisionAsync(profile, reference, token);
            _committed = true;
            if (failure == "cancel")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            }
            throw new IOException("synthetic commit acknowledgement lost");
        }
    }
}

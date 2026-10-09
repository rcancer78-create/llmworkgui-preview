using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

/// <summary>
/// The two paths that send a request to a provider must fail closed when the configured secret
/// reference cannot be used. Proceeding without credentials would test the wrong thing and would
/// report the provider's rejection instead of the real reason (ADR-0005 §5.2).
/// </summary>
public sealed class ProviderSecretGateTests
{
    private const string Reference = "urn:llmworkgui:secret:provider-key";
    private const string ProviderProfileId = "provider-1";
    private const string AccountId = "account-1";

    [Fact]
    public async Task TestConnectionAsync_RevokedReference_FailsClosedWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-stored-value";
        await server.StartAsync();

        // The payload survived the revocation, which must still make the reference unusable.
        var store = new FakeSecretStore { Secrets = { [Reference] = "sk-stored-value" } };
        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Revoked(Reference, SecretReferenceKind.ProviderApiKey, isRegistered: true));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var service = new ProviderConnectionTestService(store, secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        Assert.Contains("revoked", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_MissingReference_FailsClosedWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Missing(Reference, SecretReferenceKind.ProviderApiKey, isRegistered: true));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var service = new ProviderConnectionTestService(new FakeSecretStore(), secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_StoreReturningNothing_FailsClosedWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        // No lifecycle in the composition: an unresolvable reference must still not become an
        // unauthenticated request.
        var service = new ProviderConnectionTestService(new FakeSecretStore());

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_StoreThrowing_FailsClosedWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var store = new FakeSecretStore { ThrowOnRead = true };
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId, store);
        var service = new ProviderConnectionTestService(store, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        Assert.Equal(0, server.RequestCount);
        Assert.DoesNotContain("sk-", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_ReferenceWithoutStore_FailsClosedWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var service = new ProviderConnectionTestService();

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_ActiveReference_StillAuthenticatesTheRequest()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-stored-value";
        await server.StartAsync();

        var store = new FakeSecretStore { Secrets = { [Reference] = "sk-stored-value" } };
        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Active(Reference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var service = new ProviderConnectionTestService(store, secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_KeylessProviderWithoutReference_StillReachesTheEndpoint()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        // A provider that was never given a reference stays a keyless provider: there is nothing that
        // could have been revoked or lost, so it must keep working.
        var service = new ProviderConnectionTestService();

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl));

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_ExplicitApiKey_OverridesAnUnusableReference()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-typed-value";
        await server.StartAsync();

        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Revoked(Reference, SecretReferenceKind.ProviderApiKey, isRegistered: true));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var service = new ProviderConnectionTestService(new FakeSecretStore(), secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference),
            explicitApiKey: "sk-typed-value");

        Assert.True(result.IsSuccessful);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task ModelProbe_RevokedReference_FailsWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var store = new FakeSecretStore { Secrets = { [Reference] = "sk-stored-value" } };
        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Revoked(Reference, SecretReferenceKind.ProviderApiKey, isRegistered: true));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var executor = new ProviderModelProbeExecutor(store, secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await executor.ExecuteAsync(CreateProbeRequest(server.BaseUrl, Reference));

        Assert.Equal(ModelProbeOutcome.CredentialUnavailable, result.Outcome);
        Assert.Null(result.FailureClass);

        // Nothing was sent, so nothing may be reported as an observed endpoint or latency.
        Assert.Null(result.SanitizedEndpoint);
        Assert.Null(result.LatencyMs);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task ModelProbe_StoreReturningNothing_FailsWithoutSendingARequest()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var store = new FakeSecretStore();
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId, store);
        var executor = new ProviderModelProbeExecutor(store, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);

        var result = await executor.ExecuteAsync(CreateProbeRequest(server.BaseUrl, Reference));

        Assert.Equal(ModelProbeOutcome.CredentialUnavailable, result.Outcome);
        Assert.Null(result.FailureClass);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task ModelProbe_ActiveReference_StillRunsTheProbe()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-stored-value";
        await server.StartAsync();

        var store = new FakeSecretStore { Secrets = { [Reference] = "sk-stored-value" } };
        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Active(Reference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var executor = new ProviderModelProbeExecutor(store, secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await executor.ExecuteAsync(CreateProbeRequest(server.BaseUrl, Reference));

        Assert.Equal(ModelProbeOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task ModelProbe_KeylessProviderWithoutReference_StillRunsTheProbe()
    {
        await using var server = new LocalMockAiServer();
        await server.StartAsync();

        var executor = new ProviderModelProbeExecutor();

        var result = await executor.ExecuteAsync(CreateProbeRequest(server.BaseUrl, secretReference: null));

        Assert.Equal(ModelProbeOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task TestConnectionAsync_NeverExposesTheResolvedValueInAFailureMessage()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = "sk-super-secret-value";
        await server.StartAsync();

        var store = new FakeSecretStore { Secrets = { [Reference] = "sk-super-secret-value" } };
        var lifecycle = new FakeSecretLifecycle(SecretReferenceStatus.Active(Reference, SecretReferenceKind.ProviderApiKey, true, DateTimeOffset.UtcNow, null));
        using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId);
        var service = new ProviderConnectionTestService(store, secretLifecycle: lifecycle, profiles: configured.Profiles);

        var result = await service.TestConnectionAsync(
            new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

        Assert.DoesNotContain("sk-super-secret-value", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-super-secret-value", result.SanitizedEndpointUrl, StringComparison.Ordinal);
    }

    private static ModelProbeRequest CreateProbeRequest(string baseUrl, string? secretReference) =>
        new()
        {
            Scope = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o"),
            ProviderProfileId = ProviderProfileId,
            BaseUrl = baseUrl,
            ModelId = "mock-gpt-4o",
            ApiKeySecretReference = secretReference
        };

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public bool ThrowOnRead { get; set; }

        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default) =>
            Task.FromResult(SecretReference.Create(Guid.NewGuid().ToString("N")));

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("The Windows profile can no longer unprotect this payload.");
            }

            return Task.FromResult(Secrets.TryGetValue(secretReference, out var secret) ? secret : null);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Secrets.Remove(secretReference));
    }

    private sealed class FakeSecretLifecycle : ISecretLifecycleService
    {
        private readonly SecretReferenceStatus _status;

        public FakeSecretLifecycle(SecretReferenceStatus status) => _status = status;

        public Task<SecretReferenceStatus> GetStatusAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_status);

        public Task<SecretReferenceStatus> CreateAsync(string rawSecret, SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey, SecretReferenceOwnerBinding? owner = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RotateAsync(string reference, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RevokeAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> BindAsync(string reference, SecretReferenceOwnerBinding owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveProviderApiKeyAsync(ProviderProfile profile, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeProviderApiKeyAsync(string providerProfileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAccountSecretAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

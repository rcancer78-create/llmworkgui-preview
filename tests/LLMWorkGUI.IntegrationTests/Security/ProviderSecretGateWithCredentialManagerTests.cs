using System.Runtime.Versioning;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.IntegrationTests.Providers;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.MockServers;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

/// <summary>
/// The two provider paths that send a request, driven through the production store with a
/// Credential Manager that cannot answer.
///
/// A legacy DPAPI payload for the same reference exists in every case, so a store that degraded to
/// the fallback would authenticate successfully and hide the outage. Both paths have to refuse
/// instead, because an unauthenticated request is worse than a refused one (ADR-0005 §5.2).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProviderSecretGateWithCredentialManagerTests
{
    private const string Reference = "urn:llmworkgui:secret:provider-key";
    private const string ProviderProfileId = "provider-1";
    private const string AccountId = "account-1";
    private const string LegacyValue = "sk-legacy-value-0001";

    [Fact]
    public async Task TestConnectionAsync_CredentialManagerAccessDenied_FailsClosedWithoutARequest()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = LegacyValue;
        await server.StartAsync();

        var directory = new TestDirectory();
        var store = new CredentialManagerSecretStore(
            new FakeCredentialManagerApi { ReadOutcomeOverride = CredentialManagerOutcome.AccessDenied },
            directory.GetPath("secrets"));

        try
        {
            await new DpapiSecretStore(directory.GetPath("secrets")).OverwriteSecretAsync(Reference, LegacyValue);
            using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId, store);
            var service = new ProviderConnectionTestService(store, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);

            var result = await service.TestConnectionAsync(
                new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

            Assert.False(result.IsSuccessful);
            Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
            Assert.Equal(0, server.RequestCount);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task ModelProbe_CredentialManagerNoLogonSession_FailsClosedWithoutARequest()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = LegacyValue;
        await server.StartAsync();

        var directory = new TestDirectory();
        var store = new CredentialManagerSecretStore(
            new FakeCredentialManagerApi { ReadOutcomeOverride = CredentialManagerOutcome.NoLogonSession },
            directory.GetPath("secrets"));

        try
        {
            await new DpapiSecretStore(directory.GetPath("secrets")).OverwriteSecretAsync(Reference, LegacyValue);
            using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId, store);
            var executor = new ProviderModelProbeExecutor(store, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);

            var result = await executor.ExecuteAsync(CreateProbeRequest(server.BaseUrl, Reference));

            Assert.Equal(ModelProbeOutcome.CredentialUnavailable, result.Outcome);
            Assert.Null(result.FailureClass);
            Assert.Equal(0, server.RequestCount);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task TestConnectionAsync_CorruptCredential_FailsClosedWithoutARequest()
    {
        await using var server = new LocalMockAiServer();
        server.ExpectedApiKey = LegacyValue;
        await server.StartAsync();

        var directory = new TestDirectory();
        var credentials = new FakeCredentialManagerApi
        {
            ReadOutcomeOverride = CredentialManagerOutcome.Success,
            ReadBlobOverride = new byte[] { 0xC3, 0x28 }
        };

        var store = new CredentialManagerSecretStore(credentials, directory.GetPath("secrets"));

        try
        {
            await new DpapiSecretStore(directory.GetPath("secrets")).OverwriteSecretAsync(Reference, LegacyValue);
            using var configured = await ProviderCredentialFixture.CreateAsync(Reference, ProviderProfileId, store);
            var service = new ProviderConnectionTestService(store, secretLifecycle: configured.Lifecycle, profiles: configured.Profiles);

            var result = await service.TestConnectionAsync(
                new CustomProviderSettings(ProviderProfileId, "Provider", server.BaseUrl, apiKeySecretRef: Reference));

            Assert.Equal(ProviderConnectionStatus.SecretUnavailable, result.Status);
            Assert.Equal(0, server.RequestCount);
        }
        finally
        {
            directory.Dispose();
        }
    }

    private static ModelProbeRequest CreateProbeRequest(string baseUrl, string secretReference) =>
        new()
        {
            Scope = HealthScope.ForModelRoute(AccountId, "mock-gpt-4o"),
            ProviderProfileId = ProviderProfileId,
            BaseUrl = baseUrl,
            ModelId = "mock-gpt-4o",
            ApiKeySecretReference = secretReference
        };
}

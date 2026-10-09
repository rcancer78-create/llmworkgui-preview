using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;

namespace LLMWorkGUI.IntegrationTests.Providers;

internal sealed class ProviderCredentialFixture : IDisposable
{
    private readonly TestDatabase _database = new();
    public SqliteProviderProfileRepository Profiles { get; private set; } = null!;
    public SecretLifecycleService Lifecycle { get; private set; } = null!;

    public static async Task<ProviderCredentialFixture> CreateAsync(string reference, string providerId, ISecretStore? store = null)
    {
        var fixture = new ProviderCredentialFixture();
        try
        {
            await fixture._database.InitializeAsync();
            fixture.Profiles = new(fixture._database.Factory);
            await fixture.Profiles.UpsertAsync(new(providerId, "Provider", BackendType.OpenCode,
                "https://provider.test", null, DataClassification.PrivateSource, true), reference);
            var metadata = new SqliteSecretReferenceRepository(fixture._database.Factory);
            await metadata.InsertAsync(new SecretReferenceMetadata
            {
                Reference = reference, Kind = SecretReferenceKind.ProviderApiKey,
                State = SecretReferenceState.Active, CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await metadata.AddOwnerAsync(SecretReferenceOwnerBinding.ForProviderProfile(reference, providerId));
            if (store is not null) fixture.Lifecycle = new(store, metadata, fixture.Profiles);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public void Dispose() { Lifecycle?.Dispose(); _database.Dispose(); }
}

using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class SecretReferenceRepositoryTests : IDisposable
{
    private const string SharedReference = "urn:llmworkgui:secret:shared-key";
    private const string ProfileReference = "urn:llmworkgui:secret:profile-key";

    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task InsertAndGet_RoundTripsReferenceMetadata()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);
        var createdAt = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

        await repository.InsertAsync(new SecretReferenceMetadata
        {
            Reference = ProfileReference,
            Kind = SecretReferenceKind.ProviderApiKey,
            State = SecretReferenceState.Active,
            CreatedAtUtc = createdAt
        });

        var stored = await repository.GetAsync(ProfileReference);

        Assert.NotNull(stored);
        Assert.Equal(ProfileReference, stored!.Reference);
        Assert.Equal(SecretReferenceKind.ProviderApiKey, stored.Kind);
        Assert.Equal(SecretReferenceState.Active, stored.State);
        Assert.Equal(createdAt, stored.CreatedAtUtc);
        Assert.Null(stored.LastRotatedAtUtc);
        Assert.Null(stored.RevokedAtUtc);
        Assert.False(stored.IsRevoked);
    }

    [Fact]
    public async Task MarkRotatedAndRevoked_UpdateTimestampsWithoutTouchingTheReference()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);
        await repository.InsertAsync(new SecretReferenceMetadata
        {
            Reference = ProfileReference,
            Kind = SecretReferenceKind.ProviderApiKey,
            State = SecretReferenceState.Active,
            CreatedAtUtc = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero)
        });

        var rotatedAt = new DateTimeOffset(2026, 9, 23, 11, 30, 0, TimeSpan.Zero);
        await repository.MarkRotatedAsync(ProfileReference, rotatedAt);

        var afterRotation = await repository.GetAsync(ProfileReference);
        Assert.Equal(rotatedAt, afterRotation!.LastRotatedAtUtc);
        Assert.Equal(SecretReferenceState.Active, afterRotation.State);

        var revokedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        await repository.UpdateStateAsync(ProfileReference, SecretReferenceState.Revoked, revokedAt);

        var afterRevocation = await repository.GetAsync(ProfileReference);
        Assert.Equal(SecretReferenceState.Revoked, afterRevocation!.State);
        Assert.True(afterRevocation.IsRevoked);
        Assert.Equal(revokedAt, afterRevocation.RevokedAtUtc);
        Assert.Equal(rotatedAt, afterRevocation.LastRotatedAtUtc);
    }

    [Fact]
    public async Task Owners_MayShareOneReferenceAndBindingIsIdempotent()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);
        await repository.InsertAsync(new SecretReferenceMetadata
        {
            Reference = SharedReference,
            Kind = SecretReferenceKind.ProviderApiKey,
            State = SecretReferenceState.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // A single URN bound by a profile and by an account must not violate any invariant.
        await repository.AddOwnerAsync(SecretReferenceOwnerBinding.ForProviderProfile(SharedReference, "profile-1"));
        await repository.AddOwnerAsync(SecretReferenceOwnerBinding.ForAccount(SharedReference, "account-1"));
        await repository.AddOwnerAsync(SecretReferenceOwnerBinding.ForAccount(SharedReference, "account-1"));

        var owners = await repository.ListOwnersAsync(SharedReference);

        Assert.Equal(2, owners.Count);
        Assert.Contains(owners, owner => owner.OwnerKind == SecretReferenceOwnerKind.ProviderProfile && owner.OwnerId == "profile-1");
        Assert.Contains(owners, owner => owner.OwnerKind == SecretReferenceOwnerKind.Account && owner.OwnerId == "account-1");
    }

    [Fact]
    public async Task Delete_RemovesReferenceAndItsOwnerBindings()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);
        await repository.InsertAsync(new SecretReferenceMetadata
        {
            Reference = SharedReference,
            Kind = SecretReferenceKind.ProviderApiKey,
            State = SecretReferenceState.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await repository.AddOwnerAsync(SecretReferenceOwnerBinding.ForProviderProfile(SharedReference, "profile-1"));

        Assert.True(await repository.DeleteAsync(SharedReference));
        Assert.Null(await repository.GetAsync(SharedReference));
        Assert.Empty(await repository.ListOwnersAsync(SharedReference));
        Assert.False(await repository.DeleteAsync(SharedReference));
    }

    [Fact]
    public async Task List_ReturnsEveryRegisteredReference()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);

        foreach (var reference in new[] { ProfileReference, SharedReference })
        {
            await repository.InsertAsync(new SecretReferenceMetadata
            {
                Reference = reference,
                Kind = SecretReferenceKind.Unspecified,
                State = SecretReferenceState.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        var all = await repository.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { ProfileReference, SharedReference }, all.Select(metadata => metadata.Reference));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownReference()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);

        Assert.Null(await repository.GetAsync("urn:llmworkgui:secret:never-registered"));
        Assert.Empty(await repository.ListOwnersAsync("urn:llmworkgui:secret:never-registered"));
    }

    [Fact]
    public async Task InsertAsync_RejectsDuplicateReference()
    {
        await _database.InitializeAsync();

        var repository = new SqliteSecretReferenceRepository(_database.Factory);
        var metadata = new SecretReferenceMetadata
        {
            Reference = ProfileReference,
            Kind = SecretReferenceKind.ProviderApiKey,
            State = SecretReferenceState.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repository.InsertAsync(metadata);

        // A second row for the same URN would make the state ambiguous, so it is refused.
        await Assert.ThrowsAnyAsync<Exception>(() => repository.InsertAsync(metadata));
    }
}

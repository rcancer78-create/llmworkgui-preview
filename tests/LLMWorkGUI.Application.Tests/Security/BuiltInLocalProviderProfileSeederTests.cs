using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Tests.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Security;

public sealed class BuiltInLocalProviderProfileSeederTests
{
    [Fact]
    public async Task EnsureAsync_EmptyRepository_SeedsThreeEnabledPrivateSourceProfiles()
    {
        var repository = new InMemoryProviderProfileRepository();

        await BuiltInLocalProviderProfileSeeder.EnsureAsync(repository);

        Assert.Equal(3, (await repository.ListAsync()).Count);

        var openCode = await repository.GetByIdAsync(BuiltInLocalProviderProfileSeeder.OpenCodeProfileId);
        Assert.NotNull(openCode);
        Assert.Equal("OpenCode (local)", openCode!.DisplayName);
        Assert.Equal(BackendType.OpenCode, openCode.Backend);
        Assert.Equal(DataClassification.PrivateSource, openCode.MaxDataClass);
        Assert.True(openCode.IsEnabled);

        var cursor = await repository.GetByIdAsync(BuiltInLocalProviderProfileSeeder.CursorProfileId);
        Assert.NotNull(cursor);
        Assert.Equal("Cursor ACP (local)", cursor!.DisplayName);
        Assert.Equal(BackendType.CursorAcp, cursor.Backend);
        Assert.Equal(DataClassification.PrivateSource, cursor.MaxDataClass);
        Assert.True(cursor.IsEnabled);

        var mirasim = await repository.GetByIdAsync(BuiltInLocalProviderProfileSeeder.MirasimProfileId);
        Assert.NotNull(mirasim);
        Assert.Equal("Mirasim (local)", mirasim!.DisplayName);
        Assert.Equal(BackendType.Mirasim, mirasim.Backend);
        Assert.Equal(DataClassification.PrivateSource, mirasim.MaxDataClass);
        Assert.True(mirasim.IsEnabled);
    }

    [Fact]
    public async Task EnsureAsync_ExistingProfile_IsNotOverwritten()
    {
        var repository = new InMemoryProviderProfileRepository();

        var operatorProfile = new ProviderProfile(
            BuiltInLocalProviderProfileSeeder.CursorProfileId,
            "Operator-managed Cursor profile",
            BackendType.CursorAcp,
            "http://127.0.0.1:12345/",
            null,
            DataClassification.PublicSource,
            false);

        repository.Save(operatorProfile);

        await BuiltInLocalProviderProfileSeeder.EnsureAsync(repository);

        var cursor = await repository.GetByIdAsync(BuiltInLocalProviderProfileSeeder.CursorProfileId);

        // Operator changes are preserved: the seeder only fills gaps and never widens a ceiling.
        Assert.Same(operatorProfile, cursor);
        Assert.Equal(DataClassification.PublicSource, cursor!.MaxDataClass);
        Assert.False(cursor.IsEnabled);

        Assert.Equal(3, (await repository.ListAsync()).Count);
    }

    [Fact]
    public async Task EnsureAsync_SecondRun_IsIdempotent()
    {
        var repository = new InMemoryProviderProfileRepository();

        await BuiltInLocalProviderProfileSeeder.EnsureAsync(repository);
        var firstRun = await repository.ListAsync();

        await BuiltInLocalProviderProfileSeeder.EnsureAsync(repository);
        var secondRun = await repository.ListAsync();

        Assert.Equal(3, secondRun.Count);

        foreach (var profile in secondRun)
        {
            var first = Assert.Single(firstRun, candidate => candidate.Id == profile.Id);

            // A second run must not replace the stored instances with freshly seeded copies.
            Assert.Same(first, profile);
        }
    }
}

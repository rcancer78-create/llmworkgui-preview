using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

/// <summary>
/// Seeds the built-in local provider profiles into the composition at startup. The profiles grant the
/// local backends their declared <see cref="DataClassification.PrivateSource"/> ceiling so the
/// fail-closed data classification gate can allow local work without weakening its lookup-first rule
/// (ТЗ §6.5, ADR-0004 §6.2). An existing profile is never modified: operator changes are preserved.
/// </summary>
public static class BuiltInLocalProviderProfileSeeder
{
    public const string OpenCodeProfileId = "default";

    public const string CursorProfileId = "cursor";

    public const string MirasimProfileId = "mirasim";

    public static async Task EnsureAsync(
        IProviderProfileRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        await EnsureProfileAsync(
            repository,
            OpenCodeProfileId,
            "OpenCode (local)",
            BackendType.OpenCode,
            cancellationToken).ConfigureAwait(false);

        await EnsureProfileAsync(
            repository,
            CursorProfileId,
            "Cursor ACP (local)",
            BackendType.CursorAcp,
            cancellationToken).ConfigureAwait(false);

        await EnsureProfileAsync(
            repository,
            MirasimProfileId,
            "Mirasim (local)",
            BackendType.Mirasim,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureProfileAsync(
        IProviderProfileRepository repository,
        string profileId,
        string displayName,
        BackendType backend,
        CancellationToken cancellationToken)
    {
        var existing = await repository
            .GetByIdAsync(profileId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return;
        }

        var profile = new ProviderProfile(
            profileId,
            displayName,
            backend,
            baseUrl: null,
            executablePath: null,
            DataClassification.PrivateSource,
            isEnabled: true);

        await repository
            .UpsertAsync(profile, apiKeySecretReference: null, cancellationToken)
            .ConfigureAwait(false);
    }
}

using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteProviderProfileRepository : IProviderProfileRepository, IProviderDeletionStore
{
    internal const string SelectColumns = """
        Id, DisplayName, Backend, BaseUrl, ExecutablePath, MaxDataClass, IsEnabled, ApiKeySecretReference, GatewayNativeId, CustomHeadersJson, Revision
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard? _instanceGuard;

    public SqliteProviderProfileRepository(ISqliteConnectionFactory connectionFactory,
        LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard? instanceGuard = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _instanceGuard = instanceGuard;
    }

    public async Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM ProviderProfiles ORDER BY DisplayName, Id;";

        var list = new List<ProviderProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadProfile(reader));
        }

        return list;
    }

    public async Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM ProviderProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProfile(reader) : null;
    }

    public async Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
        => _ = await UpsertReturningRevisionAsync(profile, apiKeySecretReference, cancellationToken).ConfigureAwait(false);

    public async Task<long> UpsertReturningRevisionAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentNullException.ThrowIfNull(profile);

        foreach (var header in profile.CustomHeaders ?? [])
        {
            if (header.SecretReference is not null ? !SecretReference.IsValid(header.SecretReference)
                : CredentialTextRedactor.IsSensitiveHeaderName(header.Name))
                throw new ArgumentException("Secret headers must be persisted as canonical references.", nameof(profile));
        }

        var timestamp = SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        _instanceGuard?.EnsureSupervisorPermitted();
        foreach (var reference in (profile.CustomHeaders ?? []).Select(h => h.SecretReference).OfType<string>().Distinct())
        {
            await using var binding = connection.CreateCommand();
            binding.Transaction = transaction;
            binding.CommandText = "SELECT Kind FROM SecretReferences WHERE Reference = $reference;";
            binding.Parameters.AddWithValue("$reference", reference);
            if (await binding.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string kind
                || kind != nameof(SecretReferenceKind.ProviderHeader))
                throw new InvalidOperationException("A secret header requires registered ProviderHeader metadata.");
            binding.CommandText = "INSERT OR IGNORE INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId) VALUES ($reference, 'ProviderProfile', $owner);";
            binding.Parameters.AddWithValue("$owner", profile.Id);
            await binding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProviderProfiles
                (Id, DisplayName, Backend, BaseUrl, ExecutablePath, MaxDataClass, IsEnabled, ApiKeySecretReference, GatewayNativeId, CustomHeadersJson, CreatedAtUtc, UpdatedAtUtc, Revision)
            SELECT $id, $displayName, $backend, $baseUrl, $executablePath, $maxDataClass, $isEnabled, $apiKeySecretRef, $gatewayNativeId, $headers, $createdAtUtc, $updatedAtUtc,
                COALESCE((SELECT LastRevision + 1 FROM ProviderProfileRevisions WHERE ProviderId=$id), 0)
            WHERE $revision IS NULL OR $revision = -1 OR EXISTS (SELECT 1 FROM ProviderProfiles WHERE Id = $id)
            ON CONFLICT (Id) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Backend = excluded.Backend,
                BaseUrl = excluded.BaseUrl,
                ExecutablePath = excluded.ExecutablePath,
                MaxDataClass = excluded.MaxDataClass,
                IsEnabled = excluded.IsEnabled,
                GatewayNativeId = excluded.GatewayNativeId,
                CustomHeadersJson = COALESCE(excluded.CustomHeadersJson, ProviderProfiles.CustomHeadersJson),
                ApiKeySecretReference = COALESCE(excluded.ApiKeySecretReference, ProviderProfiles.ApiKeySecretReference),
                Revision = ProviderProfiles.Revision + 1,
                UpdatedAtUtc = excluded.UpdatedAtUtc
            WHERE $revision IS NULL OR ProviderProfiles.Revision = $revision
            RETURNING Revision;
            """;

        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$backend", SqliteRepositorySupport.FormatEnum(profile.Backend));
        SqliteRepositorySupport.AddNullable(command, "$baseUrl", profile.BaseUrl);
        SqliteRepositorySupport.AddNullable(command, "$executablePath", profile.ExecutablePath);
        command.Parameters.AddWithValue("$maxDataClass", SqliteRepositorySupport.FormatEnum(profile.MaxDataClass));
        command.Parameters.AddWithValue("$isEnabled", profile.IsEnabled ? 1 : 0);
        SqliteRepositorySupport.AddNullable(command, "$apiKeySecretRef", apiKeySecretReference);
        SqliteRepositorySupport.AddNullable(command, "$gatewayNativeId", profile.GatewayNativeId);
        SqliteRepositorySupport.AddNullable(command, "$headers", profile.CustomHeaders is null ? null : JsonSerializer.Serialize(profile.CustomHeaders));
        command.Parameters.AddWithValue("$revision", (object?)profile.Revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", timestamp);
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long revision)
            throw new InvalidOperationException("The provider profile changed; reload it before saving.",
                new ProviderProfileWriteConflictException());
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public async Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApiKeySecretReference FROM ProviderProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string str && !string.IsNullOrWhiteSpace(str) ? str : null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var profile = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return profile is not null && await DeleteConfigurationAsync(id, profile.Revision!.Value, cancellationToken).ConfigureAwait(false);
    }

    internal static ProviderProfile ReadProfile(SqliteDataReader reader)
    {
        return new ProviderProfile(
            reader.GetString(0),
            reader.GetString(1),
            SqliteRepositorySupport.ParseEnum<BackendType>(reader.GetString(2)),
            SqliteRepositorySupport.GetNullableString(reader, 3),
            SqliteRepositorySupport.GetNullableString(reader, 4),
            SqliteRepositorySupport.ParseEnum<DataClassification>(reader.GetString(5)),
            reader.GetBoolean(6),
            gatewayNativeId: SqliteRepositorySupport.GetNullableString(reader, 8),
            customHeaders: reader.IsDBNull(9) ? [] : JsonSerializer.Deserialize<ProviderHeader[]>(reader.GetString(9))
                ?? throw new InvalidDataException("Stored provider headers are invalid."),
            revision: reader.GetInt64(10));
    }
}

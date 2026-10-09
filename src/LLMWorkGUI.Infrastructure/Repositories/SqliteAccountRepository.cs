using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteAccountRepository : IAccountRepository
{
    private const string SelectColumns = """
        Id, ProviderProfileId, DisplayName, ProviderNativeId, AuthState,
        ManualPriority, IsEnabled, Health, CooldownUntilUtc, DisabledUntilUtc,
        MaxConcurrentExecutions, ReserveThreshold, SecretReference, GatewayNativeId
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IApplicationInstanceGuard? _instanceGuard;

    public SqliteAccountRepository(ISqliteConnectionFactory connectionFactory, IApplicationInstanceGuard? instanceGuard = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _instanceGuard = instanceGuard;
    }

    public async Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Accounts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    public async Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerProfileId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Accounts WHERE ProviderProfileId = $providerProfileId ORDER BY ManualPriority DESC, DisplayName, Id;";
        command.Parameters.AddWithValue("$providerProfileId", providerProfileId);

        var list = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadAccount(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Accounts ORDER BY ProviderProfileId, ManualPriority DESC, DisplayName, Id;";

        var list = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadAccount(reader));
        }

        return list;
    }

    public async Task SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentNullException.ThrowIfNull(account);

        var timestamp = SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts (
                Id, ProviderProfileId, DisplayName, ProviderNativeId, AuthState,
                ManualPriority, IsEnabled, Health, CooldownUntilUtc, DisabledUntilUtc,
                MaxConcurrentExecutions, ReserveThreshold, SecretReference, GatewayNativeId,
                CreatedAtUtc, UpdatedAtUtc
            )
            VALUES (
                $id, $providerProfileId, $displayName, $providerNativeId, $authState,
                $manualPriority, $isEnabled, $health, $cooldownUntilUtc, $disabledUntilUtc,
                $maxConcurrentExecutions, $reserveThreshold, $secretReference, $gatewayNativeId,
                $createdAtUtc, $updatedAtUtc
            )
            ON CONFLICT (Id) DO UPDATE SET
                ProviderProfileId = excluded.ProviderProfileId,
                DisplayName = excluded.DisplayName,
                ProviderNativeId = excluded.ProviderNativeId,
                AuthState = excluded.AuthState,
                ManualPriority = excluded.ManualPriority,
                IsEnabled = excluded.IsEnabled,
                Health = excluded.Health,
                CooldownUntilUtc = excluded.CooldownUntilUtc,
                DisabledUntilUtc = excluded.DisabledUntilUtc,
                MaxConcurrentExecutions = excluded.MaxConcurrentExecutions,
                ReserveThreshold = excluded.ReserveThreshold,
                SecretReference = excluded.SecretReference,
                GatewayNativeId = excluded.GatewayNativeId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$providerProfileId", account.ProviderProfileId);
        command.Parameters.AddWithValue("$displayName", account.DisplayName);
        SqliteRepositorySupport.AddNullable(command, "$providerNativeId", account.ProviderNativeId);
        command.Parameters.AddWithValue("$authState", SqliteRepositorySupport.FormatEnum(account.AuthState));
        command.Parameters.AddWithValue("$manualPriority", account.ManualPriority);
        command.Parameters.AddWithValue("$isEnabled", account.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$health", SqliteRepositorySupport.FormatEnum(account.Health));
        SqliteRepositorySupport.AddNullable(command, "$cooldownUntilUtc", account.CooldownUntil.HasValue ? SqliteRepositorySupport.FormatTimestamp(account.CooldownUntil.Value) : null);
        SqliteRepositorySupport.AddNullable(command, "$disabledUntilUtc", account.DisabledUntil.HasValue ? SqliteRepositorySupport.FormatTimestamp(account.DisabledUntil.Value) : null);
        command.Parameters.AddWithValue("$maxConcurrentExecutions", account.MaxConcurrentExecutions);
        SqliteRepositorySupport.AddNullable(command, "$reserveThreshold", account.ReserveThreshold);
        SqliteRepositorySupport.AddNullable(command, "$secretReference", account.SecretReference);
        SqliteRepositorySupport.AddNullable(command, "$gatewayNativeId", account.GatewayNativeId);
        command.Parameters.AddWithValue("$createdAtUtc", timestamp);
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceSecretReferenceAsync(string accountId, string profileId, string? expectedReference,
        string newReference, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(newReference);
        if (!LLMWorkGUI.Application.Security.SecretReference.IsValid(newReference))
            throw new ArgumentException("Account credentials require a canonical secret reference.", nameof(newReference));
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Accounts SET SecretReference=$new,AuthState='Unknown',UpdatedAtUtc=$now
            WHERE Id=$id AND ProviderProfileId=$profile AND SecretReference IS $old
            """;
        command.Parameters.AddWithValue("$id", accountId); command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$new", newReference); SqliteRepositorySupport.AddNullable(command, "$old", expectedReference);
        command.Parameters.AddWithValue("$now", SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow));
        _instanceGuard?.EnsureSupervisorPermitted(); cancellationToken.ThrowIfCancellationRequested();
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Account credential settings changed; refresh the editor.");
        _instanceGuard?.EnsureSupervisorPermitted(); cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = """
            SELECT s.Reference FROM Accounts a JOIN SecretReferences s ON s.Reference=a.SecretReference
            WHERE a.Id=$id AND s.Kind='ProviderApiKey';
            """;
        var reference = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = "DELETE FROM Accounts WHERE Id = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (reference is not null)
        {
            command.Parameters.AddWithValue("$reference", reference);
            command.Parameters.AddWithValue("$now", SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow));
            command.CommandText = """
                SELECT 1 WHERE EXISTS (SELECT 1 FROM ProviderProfiles WHERE ApiKeySecretReference=$reference)
                  OR EXISTS (SELECT 1 FROM Accounts WHERE SecretReference=$reference)
                  OR EXISTS (SELECT 1 FROM ProviderProfiles p, json_each(COALESCE(p.CustomHeadersJson,'[]')) h
                             WHERE json_extract(h.value,'$.SecretReference')=$reference)
                  OR EXISTS (SELECT 1 FROM SecretReferenceOwners WHERE Reference=$reference AND OwnerKind='Unknown');
                """;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                command.CommandText = """
                    UPDATE SecretReferences SET State='Revoked',RevokedAtUtc=COALESCE(RevokedAtUtc,$now) WHERE Reference=$reference;
                    INSERT OR IGNORE INTO PendingSecretDeletions (Reference,RequestedAtUtc) VALUES ($reference,$now);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAuthStateAsync(string id, AuthState authState, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var timestamp = SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET AuthState = $authState, UpdatedAtUtc = $updatedAtUtc WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$authState", SqliteRepositorySupport.FormatEnum(authState));
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCooldownAsync(string id, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var timestamp = SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Accounts SET CooldownUntilUtc = $cooldownUntilUtc, UpdatedAtUtc = $updatedAtUtc WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        SqliteRepositorySupport.AddNullable(command, "$cooldownUntilUtc", cooldownUntil.HasValue ? SqliteRepositorySupport.FormatTimestamp(cooldownUntil.Value) : null);
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SecretReference FROM Accounts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", accountId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string str && !string.IsNullOrWhiteSpace(str) ? str : null;
    }

    private static Account ReadAccount(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var providerProfileId = reader.GetString(1);
        var displayName = reader.GetString(2);
        var providerNativeId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var authState = SqliteRepositorySupport.ParseEnum<AuthState>(reader.GetString(4));
        var manualPriority = reader.GetInt32(5);
        var isEnabled = reader.GetInt32(6) == 1;
        var health = SqliteRepositorySupport.ParseEnum<HealthState>(reader.GetString(7));
        var cooldownUntil = reader.IsDBNull(8) ? (DateTimeOffset?)null : SqliteRepositorySupport.ParseTimestamp(reader.GetString(8));
        var disabledUntil = reader.IsDBNull(9) ? (DateTimeOffset?)null : SqliteRepositorySupport.ParseTimestamp(reader.GetString(9));
        var maxConcurrentExecutions = reader.GetInt32(10);
        var reserveThreshold = reader.IsDBNull(11) ? (double?)null : reader.GetDouble(11);
        var secretReference = reader.IsDBNull(12) ? null : reader.GetString(12);

        return new Account(
            id,
            providerProfileId,
            displayName,
            providerNativeId,
            authState,
            manualPriority,
            isEnabled,
            health,
            cooldownUntil,
            disabledUntil,
            maxConcurrentExecutions,
            reserveThreshold,
            sessionBindings: null,
            secretReference: secretReference,
            gatewayNativeId: SqliteRepositorySupport.GetNullableString(reader, 13));
    }
}

using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed partial class OpenCodeAdaptationRuntimeRegistry
{
    public async Task<AdaptationAccountConfiguration> ReadConfigurationAsync(string profileId, string accountId,
        CancellationToken token = default)
    {
        string? reference, provider, mappedReference;
        bool owned;
        await using (var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false))
        using (var tx = connection.BeginTransaction(deferred: true))
        {
            await using (var command = Command(connection, tx, """
                SELECT a.SecretReference,m.NativeProviderId,m.SecretReference FROM Accounts a
                JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
                LEFT JOIN OpenCodeAdaptationAccountMappings m ON m.AccountId=a.Id
                WHERE a.Id=$account AND p.Id=$profile AND p.Backend='OpenCode'
                """, ("$account", accountId), ("$profile", profileId)))
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw Refused();
                reference = reader.IsDBNull(0) ? null : reader.GetString(0);
                provider = reader.IsDBNull(1) ? null : reader.GetString(1);
                mappedReference = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            owned = await HasOwnedExecutionAsync(connection, tx, accountId, token).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
        }
        // Status only: the editor never receives a stored payload or promotes account AuthState.
        var status = reference is null ? null : await secrets.GetStatusAsync(reference, token).ConfigureAwait(false);
        return new(accountId, profileId, reference, status?.State ?? SecretReferenceState.Missing,
            status is { IsRegistered: true, Kind: SecretReferenceKind.ProviderApiKey }, provider, mappedReference, owned);
    }

    public async Task SaveKeyAsync(AdaptationAccountConfiguration expected, string enteredKey, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); guard.EnsureSupervisorPermitted();
        if (string.IsNullOrWhiteSpace(enteredKey) || enteredKey.Length > 16384 || enteredKey.Any(char.IsControl)) throw Refused();
        await using (var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false))
        using (var tx = connection.BeginTransaction(deferred: false))
            await RequireConfigurationAsync(connection, tx, expected, token).ConfigureAwait(false);
        // Lifecycle serializes mint/compensation. CAS replaces the selected key and invalidates prior auth;
        // migration27 independently refuses an execution admitted after the editor's preflight.
        await secrets.SaveAccountSecretAsync(expected.AccountId, enteredKey, expected.ProviderProfileId,
            expected.SecretReference, token).ConfigureAwait(false);
    }

    public Task ConfigureProviderAsync(AdaptationAccountConfiguration expected, string nativeProviderId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ConfigureAccountCoreAsync(expected.AccountId, nativeProviderId, expected, token);
    }

    private async Task RequireConfigurationAsync(SqliteConnection c, SqliteTransaction tx,
        AdaptationAccountConfiguration expected, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var command = Command(c, tx, """
            SELECT COUNT(*) FROM Accounts a JOIN ProviderProfiles p ON p.Id=a.ProviderProfileId
            LEFT JOIN OpenCodeAdaptationAccountMappings m ON m.AccountId=a.Id
            WHERE a.Id=$account AND p.Id=$profile AND p.Backend='OpenCode' AND a.SecretReference IS $reference
              AND m.NativeProviderId IS $provider AND m.SecretReference IS $mapped
            """, ("$account", expected.AccountId), ("$profile", expected.ProviderProfileId),
            ("$reference", expected.SecretReference), ("$provider", expected.NativeProviderId), ("$mapped", expected.MappingSecretReference));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1
            || await HasOwnedExecutionAsync(c, tx, expected.AccountId, token).ConfigureAwait(false)) throw Refused();
    }

    private static async Task<bool> HasOwnedExecutionAsync(SqliteConnection c, SqliteTransaction tx, string accountId, CancellationToken token)
    {
        await using var command = Command(c, tx, """
            SELECT EXISTS(SELECT 1 FROM Sessions s WHERE s.AccountId=$account AND (
                s.ActiveExecutionId IS NOT NULL
                OR EXISTS(SELECT 1 FROM Executions e WHERE e.SessionId=s.Id AND e.EndedAtUtc IS NULL)
                OR EXISTS(SELECT 1 FROM WorkflowAdaptationRuntimeOwners o WHERE o.SessionId=s.Id AND o.State!='Terminated')))
            """, ("$account", accountId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
    }
}

using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteProviderProfileRepository
{
    public async Task<bool> DeleteConfigurationAsync(string providerId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        _instanceGuard?.EnsureSupervisorPermitted();
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", providerId);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.CommandText = "SELECT Revision FROM ProviderProfiles WHERE Id=$id";
        var actual = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (actual is null) return false;
        if ((long)actual != expectedRevision) throw new InvalidOperationException("The provider changed; reload it before deletion.");

        // Account rows cascade with the profile. Capture their credentials too, then check all
        // surviving actual bindings (even those whose historical owner metadata is incomplete).
        command.CommandText = """
            SELECT DISTINCT s.Reference FROM SecretReferences s JOIN (
                SELECT ApiKeySecretReference AS Reference, 'ProviderApiKey' AS Kind FROM ProviderProfiles WHERE Id=$id
                UNION ALL SELECT SecretReference, 'ProviderApiKey' FROM Accounts WHERE ProviderProfileId=$id
                UNION ALL SELECT json_extract(h.value,'$.SecretReference'), 'ProviderHeader'
                    FROM ProviderProfiles p, json_each(COALESCE(p.CustomHeadersJson,'[]')) h WHERE p.Id=$id
            ) used ON used.Reference=s.Reference AND used.Kind=s.Kind;
            """;
        var references = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) references.Add(reader.GetString(0));

        command.CommandText = "DELETE FROM ProviderProfiles WHERE Id=$id AND Revision=$revision";
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The provider changed; reload it before deletion.");

        command.Parameters.AddWithValue("$now", SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow));
        var referenceParameter = command.Parameters.Add("$reference", SqliteType.Text);
        foreach (var reference in references)
        {
            referenceParameter.Value = reference;
            command.CommandText = """
                SELECT 1 WHERE EXISTS (SELECT 1 FROM ProviderProfiles WHERE ApiKeySecretReference=$reference)
                  OR EXISTS (SELECT 1 FROM Accounts WHERE SecretReference=$reference)
                  OR EXISTS (SELECT 1 FROM ProviderProfiles p, json_each(COALESCE(p.CustomHeadersJson,'[]')) h
                             WHERE json_extract(h.value,'$.SecretReference')=$reference)
                  OR EXISTS (SELECT 1 FROM SecretReferenceOwners WHERE Reference=$reference AND OwnerKind='Unknown');
                """;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) continue;
            command.CommandText = """
                UPDATE SecretReferences SET State='Revoked', RevokedAtUtc=COALESCE(RevokedAtUtc,$now) WHERE Reference=$reference;
                INSERT OR IGNORE INTO PendingSecretDeletions (Reference, RequestedAtUtc) VALUES ($reference,$now);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> ListPendingSecretDeletionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.Reference FROM PendingSecretDeletions p JOIN SecretReferences s ON s.Reference=p.Reference WHERE s.State='Revoked' ORDER BY p.RequestedAtUtc,p.Reference";
        var references = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) references.Add(reader.GetString(0));
        return references;
    }

    public async Task CompleteSecretDeletionAsync(string reference, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingSecretDeletions WHERE Reference=$reference";
        command.Parameters.AddWithValue("$reference", reference);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

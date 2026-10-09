using System.Globalization;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>Versioned, account-scoped payloads in the existing ModelCapabilities table.
/// Unknown legacy keys and malformed payloads cannot grant capability support.</summary>
public sealed class SqliteModelCapabilityEvidenceStore(
    ISqliteConnectionFactory factory, IApplicationInstanceGuard instanceGuard,
    SensitiveDataFilter filter, TimeProvider clock) : IModelCapabilityEvidenceStore
{
    private const string Prefix = "llmworkgui.evidence.v1/";
    private const int MaxPayloadLength = 32768;

    public async Task<ModelCapabilityContext> CaptureContextAsync(string modelId, string accountId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var (context, _) = await ReadContextAsync(connection, transaction, modelId, accountId, cancellationToken);
        transaction.Commit();
        return context;
    }

    public Task SaveAsync(ModelCapabilityEvidence evidence, ModelCapabilityContext context,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(evidence, context, false, null, cancellationToken);

    public Task SaveUserDeclarationAsync(ModelCapabilityEvidence declaration, ModelCapabilityEvidence? expected,
        ModelCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (expected is not null && (!expected.IsWellFormed || expected.ModelId != declaration.ModelId
            || expected.AccountId != declaration.AccountId)) throw new ArgumentException("Invalid expected capability scope.", nameof(expected));
        return SaveCoreAsync(declaration with { Provenance = LLMWorkGUI.Domain.Enums.ModelProvenance.UserDefined,
            ObservedAtUtc = clock.GetUtcNow(), DiscoverySource = null }, context, true, expected is null ? null : JsonSerializer.Serialize(expected), cancellationToken);
    }

    private async Task SaveCoreAsync(ModelCapabilityEvidence evidence, ModelCapabilityContext context,
        bool userDeclaration, string? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(context);
        instanceGuard.EnsureSupervisorPermitted();
        // Own the collections before validation/serialization or any await.
        evidence = evidence with
        {
            ReasoningEfforts = evidence.ReasoningEfforts?.ToArray()!,
            SpeedModes = evidence.SpeedModes?.ToArray()!,
            ExecutionModes = evidence.ExecutionModes?.ToArray()!
        };
        if (!evidence.IsWellFormed || evidence.ObservedAtUtc > clock.GetUtcNow() || !Safe(evidence, filter))
            throw new ArgumentException("Invalid or unsafe model capability evidence.", nameof(evidence));
        var payload = JsonSerializer.Serialize(evidence);
        if (payload.Length > MaxPayloadLength) throw new ArgumentException("Capability evidence is too large.", nameof(evidence));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var (currentContext, backend) = await ReadContextAsync(connection, transaction,
            evidence.ModelId, evidence.AccountId, cancellationToken);
        if (context != currentContext)
            throw new InvalidOperationException("Изменён контекст модели или аккаунта. Обновите данные и повторите подтверждение возможностей.");
        if (userDeclaration && backend == "CursorAcp")
            throw new InvalidOperationException("Возможности Cursor принимаются только из discovery. Ручная запись недоступна.");
        var key = Key(evidence.AccountId);
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT ModelId,CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc FROM ModelCapabilities WHERE ModelId=$model AND CapabilityKey=$key";
            previous.Parameters.AddWithValue("$model", evidence.ModelId);
            previous.Parameters.AddWithValue("$key", key);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var stored = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var current = ReadEvidence(reader, filter);
                if (userDeclaration && (expected is null || current is null || JsonSerializer.Serialize(current) != expected))
                    throw new InvalidOperationException("Возможности изменены другим действием. Обновите данные перед сохранением.");
                var old = Decode(stored);
                if (old is not null && (old.ObservedAtUtc > evidence.ObservedAtUtc
                    || (old.ObservedAtUtc == evidence.ObservedAtUtc && (current is null || JsonSerializer.Serialize(old) != payload))))
                    throw new InvalidOperationException("A newer or conflicting capability report is already saved.");
            }
            else if (userDeclaration && expected is not null)
                throw new InvalidOperationException("Подтверждение возможностей удалено. Обновите данные перед сохранением.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ModelCapabilities (Id,ModelId,CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc)
            VALUES ($id,$model,$key,$payload,$state,$source,$observed)
            ON CONFLICT(ModelId,CapabilityKey) DO UPDATE SET CapabilityValue=excluded.CapabilityValue,
                State=excluded.State,Provenance=excluded.Provenance,UpdatedAtUtc=excluded.UpdatedAtUtc
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$model", evidence.ModelId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$state", evidence.State.ToString());
        command.Parameters.AddWithValue("$source", evidence.Provenance.ToString());
        command.Parameters.AddWithValue("$observed", evidence.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit();
    }

    private static async Task<(ModelCapabilityContext Context, string Backend)> ReadContextAsync(
        SqliteConnection connection, SqliteTransaction transaction, string modelId, string accountId,
        CancellationToken cancellationToken)
    {
        await using var scope = connection.CreateCommand();
        scope.Transaction = transaction;
        scope.CommandText = """
            SELECT m.CapabilityRevision,a.CapabilityRevision,p.Backend
            FROM Models m JOIN Accounts a ON a.ProviderProfileId=m.ProviderProfileId
            JOIN ProviderProfiles p ON p.Id=m.ProviderProfileId AND p.Backend=m.Backend
            WHERE m.Id=$model AND a.Id=$account
            """;
        scope.Parameters.AddWithValue("$model", modelId);
        scope.Parameters.AddWithValue("$account", accountId);
        await using var reader = await scope.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The account and model must belong to the same provider/backend.");
        var modelRevision = reader.GetString(0); var accountRevision = reader.GetString(1);
        if (!Guid.TryParseExact(modelRevision, "N", out _) || !Guid.TryParseExact(accountRevision, "N", out _))
            throw new InvalidOperationException("Недоступен контекст модели или аккаунта. Обновите данные перед сохранением.");
        return (new(modelId, accountId, modelRevision, accountRevision), reader.GetString(2));
    }

    internal static async Task<IReadOnlyList<ModelCapabilityEvidence>> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, SensitiveDataFilter filter, CancellationToken token)
    {
        var results = new List<ModelCapabilityEvidence>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ModelId,CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc
            FROM ModelCapabilities WHERE CapabilityKey LIKE 'llmworkgui.evidence.v1/%'
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var evidence = ReadEvidence(reader, filter);
            if (evidence is null) continue;
            results.Add(evidence with
            {
                ReasoningEfforts = Array.AsReadOnly(evidence.ReasoningEfforts.ToArray()),
                SpeedModes = Array.AsReadOnly(evidence.SpeedModes.ToArray()),
                ExecutionModes = Array.AsReadOnly(evidence.ExecutionModes.ToArray())
            });
        }
        return results.AsReadOnly();
    }

    private static ModelCapabilityEvidence? ReadEvidence(SqliteDataReader reader, SensitiveDataFilter filter)
    {
        var evidence = reader.IsDBNull(2) ? null : Decode(reader.GetString(2));
        return evidence is null || !Safe(evidence, filter) || evidence.ModelId != reader.GetString(0)
            || Key(evidence.AccountId) != reader.GetString(1) || evidence.State.ToString() != reader.GetString(3)
            || evidence.Provenance.ToString() != reader.GetString(4)
            || !DateTimeOffset.TryParseExact(reader.GetString(5), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var observed) || observed != evidence.ObservedAtUtc ? null : evidence;
    }

    private static ModelCapabilityEvidence? Decode(string value)
    {
        if (value.Length > MaxPayloadLength) return null;
        try
        {
            var evidence = JsonSerializer.Deserialize<ModelCapabilityEvidence>(value);
            return evidence is { IsWellFormed: true } ? evidence : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Safe(ModelCapabilityEvidence evidence, SensitiveDataFilter filter) =>
        new[] { evidence.ModelId, evidence.AccountId, evidence.DiscoverySource ?? "" }.Concat(evidence.ReasoningEfforts)
            .Concat(evidence.SpeedModes).Concat(evidence.ExecutionModes).All(value => filter.Redact(value) == value);

    private static string Key(string accountId) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(accountId));
}

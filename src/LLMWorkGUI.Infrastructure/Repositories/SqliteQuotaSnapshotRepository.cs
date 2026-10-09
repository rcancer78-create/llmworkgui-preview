using System.Text.Json;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteQuotaSnapshotRepository : IQuotaSnapshotRepository
{
    private const string SelectColumns = """
        Id, AccountId, ModelId, Bucket, LimitValue, RemainingValue, UsedValue,
        ResetAtUtc, Freshness, Source, CapturedAtUtc, RawRedactedPayloadJson
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteQuotaSnapshotRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM QuotaSnapshots WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    public async Task<QuotaSnapshot?> GetLatestForAccountAsync(
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (modelId != null)
        {
            command.CommandText = $"""
                SELECT {SelectColumns} FROM QuotaSnapshots
                WHERE AccountId = $accountId AND ModelId = $modelId
                ORDER BY CapturedAtUtc DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$modelId", modelId);
        }
        else
        {
            command.CommandText = $"""
                SELECT {SelectColumns} FROM QuotaSnapshots
                WHERE AccountId = $accountId
                ORDER BY CapturedAtUtc DESC LIMIT 1;
                """;
        }

        command.Parameters.AddWithValue("$accountId", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    public async Task<QuotaSnapshot?> GetLatestAccountWideAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM QuotaSnapshots WHERE AccountId = $accountId AND ModelId IS NULL ORDER BY CapturedAtUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM QuotaSnapshots
            WHERE AccountId = $accountId
            ORDER BY CapturedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);

        var list = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadSnapshot(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM QuotaSnapshots
            WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid, ROW_NUMBER() OVER (
                        PARTITION BY AccountId ORDER BY CapturedAtUtc DESC, rowid DESC) AS LatestRank
                    FROM QuotaSnapshots
                ) WHERE LatestRank = 1
            )
            ORDER BY AccountId;
            """;

        var list = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadSnapshot(reader));
        }

        return list;
    }

    public async Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var primary = snapshot.PrimaryBucket;
        var capturedAtUtc = SqliteRepositorySupport.FormatTimestamp(snapshot.CapturedAt);
        var resetAtUtc = primary?.ResetAt.HasValue == true
            ? SqliteRepositorySupport.FormatTimestamp(primary.ResetAt.Value)
            : null;

        var payloadJson = SerializePayload(snapshot);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO QuotaSnapshots (
                Id, AccountId, ModelId, Bucket, LimitValue, RemainingValue, UsedValue,
                ResetAtUtc, Freshness, Source, CapturedAtUtc, RawRedactedPayloadJson
            )
            VALUES (
                $id, $accountId, $modelId, $bucket, $limitValue, $remainingValue, $usedValue,
                $resetAtUtc, $freshness, $source, $capturedAtUtc, $payloadJson
            )
            ON CONFLICT (Id) DO UPDATE SET
                ModelId = excluded.ModelId,
                Bucket = excluded.Bucket,
                LimitValue = excluded.LimitValue,
                RemainingValue = excluded.RemainingValue,
                UsedValue = excluded.UsedValue,
                ResetAtUtc = excluded.ResetAtUtc,
                Freshness = excluded.Freshness,
                Source = excluded.Source,
                CapturedAtUtc = excluded.CapturedAtUtc,
                RawRedactedPayloadJson = excluded.RawRedactedPayloadJson;
            """;

        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$accountId", snapshot.AccountId);
        SqliteRepositorySupport.AddNullable(command, "$modelId", snapshot.ModelId);
        SqliteRepositorySupport.AddNullable(command, "$bucket",
            primary is null ? null : QuotaDiagnosticRedactor.Redact(primary.BucketName));
        SqliteRepositorySupport.AddNullable(command, "$limitValue", primary?.LimitValue);
        SqliteRepositorySupport.AddNullable(command, "$remainingValue", primary?.RemainingValue);
        SqliteRepositorySupport.AddNullable(command, "$usedValue", primary?.UsedValue);
        SqliteRepositorySupport.AddNullable(command, "$resetAtUtc", resetAtUtc);
        command.Parameters.AddWithValue("$freshness", SqliteRepositorySupport.FormatEnum(snapshot.Provenance));
        command.Parameters.AddWithValue("$source", snapshot.ProviderProfileId ?? "default");
        command.Parameters.AddWithValue("$capturedAtUtc", capturedAtUtc);
        SqliteRepositorySupport.AddNullable(command, "$payloadJson", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var cutoffStr = SqliteRepositorySupport.FormatTimestamp(cutoff);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM QuotaSnapshots WHERE CapturedAtUtc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffStr);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static QuotaSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var accountId = reader.GetString(1);
        var modelId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var bucketName = reader.IsDBNull(3) ? null : QuotaDiagnosticRedactor.Redact(reader.GetString(3));
        var limitValue = reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4);
        var remainingValue = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5);
        var usedValue = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6);
        var resetAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : SqliteRepositorySupport.ParseTimestamp(reader.GetString(7));
        var provenance = SqliteRepositorySupport.ParseEnum<QuotaProvenance>(reader.GetString(8));
        var source = reader.GetString(9);
        var capturedAt = SqliteRepositorySupport.ParseTimestamp(reader.GetString(10));
        var payloadJson = reader.IsDBNull(11) ? null : QuotaSnapshotPayloadSanitizer.Redact(reader.GetString(11));

        var buckets = DeserializeBuckets(payloadJson, out var invalidBuckets);
        if (!invalidBuckets && buckets.Count == 0 && !HasStructuredBucketMetadata(payloadJson) && !string.IsNullOrWhiteSpace(bucketName))
        {
            buckets = new List<QuotaBucket>
            {
                new(
                    bucketName,
                    QuotaLimitUnit.Requests,
                    QuotaLimitWindow.PerDay,
                    limitValue,
                    usedValue,
                    remainingValue,
                    resetAt)
            };
        }

        string? errorMessage = null;
        DateTimeOffset? expiresAt = null;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("errorMessage", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                {
                    errorMessage = errProp.GetString();
                }
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("expiresAtUtc", out var expProp) && expProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expProp.GetString(), out var expiration))
                {
                    expiresAt = expiration;
                }
            }
            catch (JsonException)
            {
                // Fallback on corrupt payload
            }
        }

        if (invalidBuckets)
        {
            // A partial set can remove the restrictive bucket while retaining a numeric score.
            // Corrupt structured evidence is diagnostic only, regardless of the stored provenance.
            provenance = QuotaProvenance.Error;
            errorMessage = "Stored quota bucket metadata is invalid; quota evidence is unavailable.";
        }

        return new QuotaSnapshot(
            id,
            accountId,
            provenance,
            capturedAt,
            buckets,
            providerProfileId: source,
            modelId: modelId,
            expiresAt: expiresAt,
            rawRedactedPayloadJson: payloadJson,
            errorMessage: errorMessage);
    }

    internal static string SerializePayload(QuotaSnapshot snapshot)
    {
        var bucketsData = snapshot.Buckets.Select(b => new
        {
            name = QuotaDiagnosticRedactor.Redact(b.BucketName),
            unit = b.Unit.ToString(),
            window = b.Window.ToString(),
            limit = b.LimitValue,
            used = b.UsedValue,
            remaining = b.RemainingValue,
            resetAt = b.ResetAt?.ToString("O"),
            hardReserve = b.HardReserve,
            confidence = b.Confidence.ToString()
        }).ToArray();

        var payload = new
        {
            errorMessage = snapshot.ErrorMessage is null ? null : QuotaDiagnosticRedactor.Redact(snapshot.ErrorMessage),
            expiresAtUtc = snapshot.ExpiresAt?.ToString("O"),
            buckets = bucketsData
        };

        return JsonSerializer.Serialize(payload);
    }

    private static List<QuotaBucket> DeserializeBuckets(string? payloadJson, out bool invalidBuckets)
    {
        invalidBuckets = false;
        if (string.IsNullOrWhiteSpace(payloadJson)) return new List<QuotaBucket>();

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("buckets", out var bucketsProp))
            {
                return new List<QuotaBucket>();
            }
            if (bucketsProp.ValueKind != JsonValueKind.Array)
            {
                invalidBuckets = true;
                return new List<QuotaBucket>();
            }

            var list = new List<QuotaBucket>();
            foreach (var elem in bucketsProp.EnumerateArray())
            {
                var name = elem.GetProperty("name").GetString() ?? "default";
                if (!TryReadNamedEnum<QuotaLimitUnit>(elem, "unit", out var unit)
                    || !TryReadNamedEnum<QuotaLimitWindow>(elem, "window", out var window)
                    || !TryReadNamedEnum<QuotaConfidence>(elem, "confidence", out var confidence))
                {
                    invalidBuckets = true;
                    return new List<QuotaBucket>();
                }
                var limit = ReadOptionalNumber(elem, "limit");
                var used = ReadOptionalNumber(elem, "used");
                var remaining = ReadOptionalNumber(elem, "remaining");
                var resetAt = elem.TryGetProperty("resetAt", out var rsp) && rsp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(rsp.GetString(), out var dt) ? dt : (DateTimeOffset?)null;
                var hardReserve = ReadOptionalNumber(elem, "hardReserve");
                list.Add(new QuotaBucket(name, unit, window, limit, used, remaining, resetAt, hardReserve, confidence));
            }

            return list;
        }
        catch (Exception)
        {
            invalidBuckets = true;
            return new List<QuotaBucket>();
        }
    }

    private static double? ReadOptionalNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("Stored quota bucket metadata is invalid.");
        return field.GetDouble();
    }

    private static bool TryReadNamedEnum<TEnum>(JsonElement element, string property, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (!element.TryGetProperty(property, out var field) || field.ValueKind != JsonValueKind.String) return false;
        var text = field.GetString();
        return Enum.TryParse(text, out value) && Enum.IsDefined(value)
            && string.Equals(text, value.ToString(), StringComparison.Ordinal);
    }

    private static bool HasStructuredBucketMetadata(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("buckets", out _);
        }
        catch (JsonException) { return true; } // Corrupt modern metadata cannot authorize a guessed legacy quota.
    }
}

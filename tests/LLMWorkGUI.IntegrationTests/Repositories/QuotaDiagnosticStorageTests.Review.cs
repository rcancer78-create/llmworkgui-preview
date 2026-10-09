using System.Text.Json.Nodes;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed partial class QuotaDiagnosticStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_NestedBucketCredentialCannotReachDurableOrReadPayload(bool legacy)
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        var bucket = new QuotaBucket(legacy ? "ordinary" : "Authorization: Bearer " + Secret,
            QuotaLimitUnit.Tokens, QuotaLimitWindow.PerDay, 100, 10, 90);
        await repository.SaveAsync(new QuotaSnapshot("quota-storage", "account-1",
            QuotaProvenance.ExactProviderReported, Captured, new[] { bucket }, providerProfileId: "provider-1"));
        if (legacy)
        {
            var payload = JsonNode.Parse(await ReadPayloadAsync(database.Factory))!;
            payload["buckets"]![0]!["name"] = "Authorization: Bearer " + Secret;
            await using var connection = await database.Factory.OpenConnectionAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='quota-storage';";
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            await update.ExecuteNonQueryAsync();
        }
        else Assert.DoesNotContain(Secret, await ReadPayloadAsync(database.Factory), StringComparison.Ordinal);

        var read = Assert.IsType<QuotaSnapshot>(await repository.GetByIdAsync("quota-storage"));
        Assert.DoesNotContain(Secret, read.RawRedactedPayloadJson!, StringComparison.Ordinal);
        var readBucket = Assert.Single(read.Buckets);
        Assert.DoesNotContain(Secret, readBucket.BucketName, StringComparison.Ordinal);
        Assert.Equal(90d, readBucket.RemainingValue);
    }

    [Theory]
    [InlineData("confidence", "future")]
    [InlineData("confidence", null)]
    [InlineData("unit", "future")]
    [InlineData("unit", null)]
    [InlineData("window", "999")]
    [InlineData("window", null)]
    public async Task Review_UnrecognizedBucketMetadataCannotBecomeUsableExactQuota(string field, string? value)
    {
        using var database = await CreateDatabaseAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(CreateSnapshot());
        var payload = JsonNode.Parse(await ReadPayloadAsync(database.Factory))!;
        payload["buckets"]![0]![field] = value;
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='quota-storage';";
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            await update.ExecuteNonQueryAsync();
        }
        var read = Assert.IsType<QuotaSnapshot>(await repository.GetByIdAsync("quota-storage"));
        Assert.Empty(read.Buckets);
    }

    [Fact]
    public async Task Review_UndefinedPersistedAccountHealthIsRefused()
    {
        using var database = await CreateDatabaseAsync();
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE Accounts SET Health='999' WHERE Id='account-1';";
            await update.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteAccountRepository(database.Factory).GetByIdAsync("account-1"));
    }
}

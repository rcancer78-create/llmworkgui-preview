using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class QuotaMetadataCorrectiveReviewTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("\"ordinary metadata\"")]
    [InlineData("17")]
    [InlineData("null")]
    [InlineData("{\"expiresAtUtc\":\"not-a-date\"}")]
    public async Task LegacyJsonMetadataCannotCrashPublicSnapshotReads(string payload)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(new QuotaSnapshot("snapshot", "account-1", QuotaProvenance.Unknown, DateTimeOffset.UnixEpoch));
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='snapshot'";
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }
        var snapshot = await repository.GetByIdAsync("snapshot");
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.ExpiresAt);
        Assert.Null(snapshot.ErrorMessage);
        Assert.Equal(payload, snapshot.RawRedactedPayloadJson);
    }

    [Fact]
    public async Task EqualCaptureTimesStillHaveOneDeterministicLatestSnapshotPerAccount()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(new QuotaSnapshot("first", "account-1", QuotaProvenance.Unknown, DateTimeOffset.UnixEpoch));
        await repository.SaveAsync(new QuotaSnapshot("second", "account-1", QuotaProvenance.Unknown, DateTimeOffset.UnixEpoch));
        Assert.Equal("second", Assert.Single(await repository.ListAllLatestAsync()).Id);
        Assert.Equal("second", Assert.Single(await repository.ListAllLatestAsync()).Id);
    }

    [Fact]
    public void ExistingRecursiveQuotaRedactionMasksOpaqueNestedSecrets()
    {
        var cleaned = QuotaDiagnosticRedactor.Redact("{\"metadata\":[{\"api_key\":{\"opaque\":\"synthetic-private-value\"}}],\"remaining\":7}");
        Assert.DoesNotContain("synthetic-private-value", cleaned);
        Assert.Contains("REDACTED", cleaned);
        Assert.Contains("\"remaining\":7", cleaned);
    }
}

using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class QuotaStructuredEvidenceDeltaTests
{
    [Theory]
    [InlineData("unknown-unit")]
    [InlineData("missing-name")]
    [InlineData("malformed-remaining")]
    public async Task InvalidSecondBucketCannotLeaveTrustedNumericEvidenceForAutomaticRouting(string corruption)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        var engine = Engine(database, repository);
        await repository.SaveAsync(Snapshot(reserveViolated: true));

        // The actual stored second bucket excludes this account before corruption.
        var intact = Assert.IsType<QuotaSnapshot>(await repository.GetByIdAsync("owned-quota"));
        Assert.True(intact.HasHardReserveViolation);
        Assert.True(intact.CanCalculateNumericScore(DateTimeOffset.UtcNow));
        Assert.False((await engine.SelectRouteAsync(Request())).IsSuccess);

        await using (var connection = await database.Factory.OpenConnectionAsync())
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT RawRedactedPayloadJson FROM QuotaSnapshots WHERE Id='owned-quota';";
            var payload = JsonNode.Parse(Assert.IsType<string>(await read.ExecuteScalarAsync()))!.AsObject();
            var second = payload["buckets"]!.AsArray()[1]!.AsObject();
            switch (corruption)
            {
                case "unknown-unit": second["unit"] = "owned-unsupported-unit"; break;
                case "missing-name": second.Remove("name"); break;
                default: second["remaining"] = "owned-invalid-remaining"; break;
            }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$payload WHERE Id='owned-quota';";
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var observed = Assert.IsType<QuotaSnapshot>(await repository.GetByIdAsync("owned-quota"));
        var decision = await engine.SelectRouteAsync(Request());
        Assert.False(decision.IsSuccess, "A malformed reserve bucket cannot authorize an automatic route.");
        Assert.False(observed.IsTrusted, "Invalid structured metadata cannot retain provider-exact provenance.");
        Assert.False(observed.CanCalculateNumericScore(DateTimeOffset.UtcNow));
        Assert.Empty(observed.Buckets); // Neither a partial first bucket nor legacy scalar reconstruction is evidence.
        Assert.DoesNotContain("owned-invalid", observed.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain("owned-unsupported", observed.ErrorMessage ?? string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidStructuredBucketsPreserveActualReserveAndAutomaticRouting(bool reserveViolated)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        await repository.SaveAsync(Snapshot(reserveViolated));

        var observed = Assert.IsType<QuotaSnapshot>(await repository.GetByIdAsync("owned-quota"));
        Assert.True(observed.IsTrusted);
        Assert.True(observed.CanCalculateNumericScore(DateTimeOffset.UtcNow));
        Assert.Equal(2, observed.Buckets.Count);
        Assert.Equal(reserveViolated, observed.HasHardReserveViolation);
        Assert.Null(observed.ErrorMessage);
        var decision = await Engine(database, repository).SelectRouteAsync(Request());
        Assert.Equal(!reserveViolated, decision.IsSuccess);
        if (!reserveViolated) Assert.Equal("account-1", decision.SelectedAccount!.Id);
    }

    private static QuotaSnapshot Snapshot(bool reserveViolated) => new(
        "owned-quota", "account-1", QuotaProvenance.ExactProviderReported, DateTimeOffset.UtcNow,
        [new QuotaBucket("healthy-primary", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay,
            limitValue: 100, remainingValue: 90, hardReserve: 10),
         new QuotaBucket("secondary-reserve", QuotaLimitUnit.Tokens, QuotaLimitWindow.PerMinute,
            limitValue: 100, remainingValue: reserveViolated ? 2 : 80, hardReserve: 10)],
        providerProfileId: "provider-1", modelId: "model-1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

    private static RoutingEngine Engine(TestDatabase database, SqliteQuotaSnapshotRepository repository) =>
        new(new SqliteAccountRepository(database.Factory), repository,
            providerProfileRepository: new SqliteProviderProfileRepository(database.Factory));

    private static RouteSelectionRequest Request() => new()
    {
        Backend = BackendType.OpenCode, ProviderProfileId = "provider-1", ModelId = "model-1",
        Policy = RoutingPolicy.QuotaFirst, ProjectDataClass = DataClassification.PrivateSource
    };
}

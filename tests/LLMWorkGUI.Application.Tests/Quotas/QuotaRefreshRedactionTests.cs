using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaRefreshRedactionTests
{
    // Deliberately synthetic values; never read credentials or user provider data.
    private const string Secret = "synthetic-quota-redaction-value";

    [Fact]
    public async Task ResponseForAnotherModel_IsNotPersistedOrPublishedAsValidQuota()
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        var foreign = Snapshot(QuotaProvenance.ExactProviderReported, "{}");
        using var scheduler = Create(new Adapter { Result = foreign }, repository);
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "another-model", force: true);
        Assert.Equal(QuotaProvenance.Error, result.Provenance);
        Assert.Equal("another-model", result.ModelId);
        Assert.Null(await repository.GetByIdAsync(foreign.Id));
    }

    [Theory]
    [InlineData("token=a", "token=***REDACTED***")]
    [InlineData("password=ab", "password=***REDACTED***")]
    [InlineData("api_key=abc", "api_key=***REDACTED***")]
    [InlineData("bearer tiny", "bearer ***REDACTED***")]
    public async Task LegacyShortCredentialAssignmentsStayMasked(string input, string expected)
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = Create(new Adapter { Failure = new InvalidOperationException(input) }, repository);
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.Equal(expected, result.ErrorMessage);
        Assert.Equal(expected, result.RawRedactedPayloadJson);
        Assert.Equal(expected, scheduler.GetStatus("account")!.LastError);
    }

    [Theory]
    [InlineData("token: 401")]
    [InlineData("password=a")]
    [InlineData("bearer tiny")]
    [InlineData("api_key=a\"quoted\\suffix")]
    public async Task ShortCredentialsInsideJsonStringValuesAreMaskedWithoutBreakingJson(string message)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { message, code = 401 });
        using var scheduler = Create(new Adapter { Result = Snapshot(QuotaProvenance.ExactProviderReported, payload) },
            new InMemoryQuotaSnapshotRepository());
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        using var json = System.Text.Json.JsonDocument.Parse(result.RawRedactedPayloadJson!);
        Assert.Equal(401, json.RootElement.GetProperty("code").GetInt32());
        var redactedMessage = json.RootElement.GetProperty("message").GetString();
        Assert.Contains("***REDACTED***", redactedMessage);
        Assert.DoesNotContain(message, redactedMessage);
    }

    [Theory]
    [InlineData("token=a", true)]
    [InlineData("ordinary diagnostic", false)]
    public async Task RootJsonStringRetainsJsonShapeAndCleanFormatting(string message, bool redact)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(message);
        var original = Snapshot(QuotaProvenance.ExactProviderReported, payload);
        using var scheduler = Create(new Adapter { Result = original }, new InMemoryQuotaSnapshotRepository());
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        using var json = System.Text.Json.JsonDocument.Parse(result.RawRedactedPayloadJson!);
        Assert.Equal(System.Text.Json.JsonValueKind.String, json.RootElement.ValueKind);
        if (redact) { Assert.Equal("token=***REDACTED***", json.RootElement.GetString()); }
        else { Assert.Equal(payload, result.RawRedactedPayloadJson); Assert.Same(original, result); }
    }

    [Fact]
    public async Task DuplicateJsonPropertiesAndEscapedCredentialNamesAreRedactedWithoutLosingSnapshot()
    {
        var payload = "{\"remaining\":7,\"remaining\":9,\"api_\\u006bey\":\"" + Secret
            + "\",\"api_key\":\"" + Secret + "\"}";
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = Create(new Adapter { Result = Snapshot(QuotaProvenance.ExactProviderReported, payload) }, repository);
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        AssertSafe(result.RawRedactedPayloadJson);
        using var json = System.Text.Json.JsonDocument.Parse(result.RawRedactedPayloadJson!);
        Assert.Equal(2, json.RootElement.EnumerateObject().Count(property => property.Name == "remaining"));
        Assert.Equal(9, json.RootElement.GetProperty("remaining").GetInt32());
        Assert.Same(result, await repository.GetByIdAsync(result.Id));
    }

    [Fact]
    public async Task CleanDuplicatePropertiesRetainOriginalPayloadAndSnapshot()
    {
        const string payload = "{\n  \"remaining\": 7, \"remaining\": 9\n}";
        var original = Snapshot(QuotaProvenance.ExactProviderReported, payload);
        using var scheduler = Create(new Adapter { Result = original }, new InMemoryQuotaSnapshotRepository());
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.Same(original, result);
        Assert.Equal(payload, result.RawRedactedPayloadJson);
    }

    [Fact]
    public async Task ProviderErrorPayloadRemainsWholeInStatusAndEvent()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { message = new string('x', 700), code = 401 });
        using var scheduler = Create(new Adapter { Result = Snapshot(QuotaProvenance.Error, payload) },
            new InMemoryQuotaSnapshotRepository());
        QuotaRefreshedEventArgs? notification = null;
        scheduler.QuotaRefreshed += (_, args) => notification = args;
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.Equal(payload, result.RawRedactedPayloadJson);
        Assert.Equal(payload, notification!.Error);
        Assert.Equal(payload, scheduler.GetStatus("account")!.LastError);
    }

    [Theory]
    [InlineData("see [REDACTED] docs")]
    [InlineData("token=[REDACTED]")]
    [InlineData("see ***REDACTED*** docs")]
    public async Task AlreadyRedactedOrBenignMarkersDoNotMutateCleanPayload(string note)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { note });
        var original = Snapshot(QuotaProvenance.ExactProviderReported, payload);
        using var scheduler = Create(new Adapter { Result = original }, new InMemoryQuotaSnapshotRepository());
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.Same(original, result);
        Assert.Equal(payload, result.RawRedactedPayloadJson);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("password")]
    [InlineData("credential")]
    [InlineData("access_token")]
    public async Task QuotedExceptionCredentialsNeverReachPersistedOrPublishedErrors(string field)
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        var logger = new CaptureLogger();
        using var scheduler = Create(new Adapter { Failure = new InvalidOperationException(
            "{\"" + field + "\":\"" + Secret + "\",\"message\":\"denied\"}") }, repository, logger);
        QuotaRefreshedEventArgs? notification = null;
        scheduler.QuotaRefreshed += (_, args) => notification = args;

        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);

        Assert.Equal(QuotaProvenance.Error, result.Provenance);
        AssertSafe(result.ErrorMessage);
        AssertSafe(result.RawRedactedPayloadJson);
        AssertSafe(scheduler.GetStatus("account")!.LastError);
        AssertSafe(notification!.Error);
        Assert.Same(result, notification.Snapshot);
        Assert.Same(result, await repository.GetByIdAsync(result.Id));
        Assert.Contains("***REDACTED***", result.ErrorMessage);
        Assert.All(logger.Messages, AssertSafe);
    }

    [Theory]
    [InlineData(QuotaProvenance.ExactProviderReported)]
    [InlineData(QuotaProvenance.Error)]
    public async Task AdapterPayloadAndErrorAreRedactedBeforeSaveWithoutChangingQuotaIdentity(QuotaProvenance provenance)
    {
        var original = Snapshot(provenance, "{\"credentials\":{\"password\":\"" + Secret + "\"},\"remaining\":7}",
            "api_key=" + Secret);
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = Create(new Adapter { Result = original }, repository);
        QuotaRefreshedEventArgs? notification = null;
        scheduler.QuotaRefreshed += (_, args) => notification = args;

        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);

        AssertSafe(result.RawRedactedPayloadJson);
        AssertSafe(result.ErrorMessage);
        AssertSafe(notification!.Error);
        AssertSafe(scheduler.GetStatus("account")!.LastError);
        Assert.Same(result, await repository.GetByIdAsync(result.Id));
        Assert.Same(result, notification.Snapshot);
        Assert.Equal(original.Id, result.Id);
        Assert.Equal(original.AccountId, result.AccountId);
        Assert.Equal(original.ProviderProfileId, result.ProviderProfileId);
        Assert.Equal(original.ModelId, result.ModelId);
        Assert.Equal(original.Provenance, result.Provenance);
        Assert.Equal(original.CapturedAt, result.CapturedAt);
        Assert.Equal(original.ExpiresAt, result.ExpiresAt);
        Assert.Equal(original.Buckets, result.Buckets);
        Assert.Equal(original.CanCalculateNumericScore(original.CapturedAt), result.CanCalculateNumericScore(original.CapturedAt));
    }

    [Fact]
    public async Task StructuredNonStringCredentialsAreRemovedWithoutRedactingQuotaNumbers()
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = Create(new Adapter { Result = Snapshot(QuotaProvenance.ExactProviderReported,
            "{\"password\":1234567,\"nested\":{\"credential\":[\"private-value\"]},\"remaining\":7}") }, repository);
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.DoesNotContain("1234567", result.RawRedactedPayloadJson);
        Assert.DoesNotContain("private-value", result.RawRedactedPayloadJson);
        using var json = System.Text.Json.JsonDocument.Parse(result.RawRedactedPayloadJson!);
        Assert.Equal(7, json.RootElement.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task CleanPayloadFormattingAndSnapshotInstanceArePreserved()
    {
        const string payload = "{\n  \"remaining\" : 7, \"max_tokens\":4096, \"note\":\"available\"\n}";
        var original = Snapshot(QuotaProvenance.ExactProviderReported, payload, "ordinary diagnostic");
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = Create(new Adapter { Result = original }, repository);
        var result = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.Same(original, result);
        Assert.Equal(payload, result.RawRedactedPayloadJson);
    }

    [Fact]
    public async Task CachedLegacyPayloadIsNotReturnedUnredacted()
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        var adapter = new Adapter { Result = Snapshot(QuotaProvenance.Error, "ordinary error") };
        using var scheduler = Create(adapter, repository);
        var first = await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        // Replace only the legacy diagnostic payload; cache reuse still requires the
        // same provider/account/model identity as the original refresh.
        await repository.SaveAsync(new QuotaSnapshot(first.Id, "account", QuotaProvenance.Error, first.CapturedAt,
            providerProfileId: first.ProviderProfileId, modelId: first.ModelId,
            rawRedactedPayloadJson: "{\"password\":\"" + Secret + "\"}", errorMessage: "token=" + Secret));

        var cached = await scheduler.RefreshAccountNowAsync("provider", "account", "model");

        Assert.Equal(1, adapter.Calls);
        AssertSafe(cached.RawRedactedPayloadJson);
        AssertSafe(cached.ErrorMessage);
    }

    [Fact]
    public async Task LoggerReceivesNoOriginalExceptionOrSensitiveInnerMessage()
    {
        var repository = new InMemoryQuotaSnapshotRepository();
        var logger = new CaptureLogger();
        using var scheduler = Create(new Adapter { Failure = new InvalidOperationException("ordinary error",
            new Exception("password=" + Secret)) }, repository, logger);
        await scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, AssertSafe);
        Assert.All(logger.Exceptions, exception => Assert.Null(exception));
    }

    private static void AssertSafe(string? value) => Assert.True(
        value is null || !value.Contains(Secret, StringComparison.Ordinal), "Synthetic credential reached an output boundary.");

    private static QuotaSnapshot Snapshot(QuotaProvenance provenance, string? payload, string? error = null) => new(
        "snapshot", "account", provenance, DateTimeOffset.Parse("2026-10-01T17:00:00Z"),
        new[] { new QuotaBucket("requests", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 10, 3, 7) },
        "provider", "model", DateTimeOffset.Parse("2026-10-01T18:00:00Z"), payload, error);

    private static QuotaRefreshScheduler Create(Adapter adapter, InMemoryQuotaSnapshotRepository repository,
        CaptureLogger? logger = null) => new(adapter, repository, options: Options.Create(new QuotaSchedulerOptions
        { ProviderRateLimitDelay = TimeSpan.Zero, MinRefreshInterval = TimeSpan.FromMinutes(1), JitterRatio = 0 }), logger: logger);

    private sealed class Adapter : IQuotaSourceAdapter
    {
        public string SourceKind => "fixture";
        public QuotaSnapshot? Result { get; init; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId, string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null ? Task.FromResult(Result!) : Task.FromException<QuotaSnapshot>(Failure);
        }
    }

    private sealed class CaptureLogger : ILogger<QuotaRefreshScheduler>
    {
        public List<string> Messages { get; } = new();
        public List<Exception?> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}

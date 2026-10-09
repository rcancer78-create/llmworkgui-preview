using System.Text.Json;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Logging;
using LLMWorkGUI.Infrastructure.Observability;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Security;

public sealed class StructuredDiagnosticBoundaryTests : IDisposable
{
    private const string SyntheticValue = "synthetic-structured-diagnostic-value";
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "llmworkgui-structured-diagnostic-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public StructuredDiagnosticBoundaryTests()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "fixture.db"));
    }

    public static TheoryData<string, string> Payloads => new()
    {
        { "{\"password\":123456789,\"remaining\":7}", "123456789" },
        { "{\"credential\":{\"value\":\"" + SyntheticValue + "\"},\"remaining\":7}", SyntheticValue },
        { "{\"credential\":[\"" + SyntheticValue + "\"],\"remaining\":7}", SyntheticValue },
        { "{\"api\\u005fkey\":\"" + SyntheticValue + "\",\"remaining\":7}", SyntheticValue },
        { "[{\"password\":123456789,\"remaining\":7}]", "123456789" },
        { "{\"password\":123456789,\"password\":123456789,\"remaining\":7}", "123456789" }
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void StructuredLogArgumentsAndMessageAreRedacted(string payload, string secret)
    {
        var capture = new Capture();
        var logger = new RedactingLogger(capture, new SensitiveDataFilter());
        logger.LogWarning("Backend payload {Payload}", payload);

        Assert.DoesNotContain(secret, capture.Message, StringComparison.Ordinal);
        AssertSafe(capture.Message["Backend payload ".Length..], secret);
        var values = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(capture.State);
        AssertSafe(Assert.IsType<string>(Assert.Single(values, value => value.Key == "Payload").Value), secret);

        logger.Log(LogLevel.Warning, new EventId(1), payload, null, (text, _) => text);
        AssertSafe(capture.Message, secret);
        Assert.Contains(secret, payload, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void StructuredScopeTextAndValuesAreRedacted(string payload, string secret)
    {
        var capture = new Capture();
        var logger = new RedactingLogger(capture, new SensitiveDataFilter());
        using (logger.BeginScope(payload))
        {
            AssertSafe(Assert.IsType<string>(capture.Scope), secret);
        }

        using (logger.BeginScope(new[] { new KeyValuePair<string, object?>("Payload", payload) }))
        {
            var scope = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(capture.Scope);
            AssertSafe(Assert.IsType<string>(Assert.Single(scope).Value), secret);
        }
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void StructuredRootAndNestedExceptionMessagesAreRedacted(string payload, string secret)
    {
        var capture = new Capture();
        var logger = new RedactingLogger(capture, new SensitiveDataFilter());
        var original = new InvalidOperationException(payload);
        logger.LogError(original, "Backend failed");

        var redacted = Assert.IsType<RedactedException>(capture.Exception);
        AssertSafe(redacted.Message, secret);
        Assert.Equal(nameof(InvalidOperationException), redacted.OriginalExceptionType);
        Assert.DoesNotContain(secret, redacted.ToString(), StringComparison.Ordinal);
        Assert.Equal(payload, original.Message);

        logger.LogError(new InvalidOperationException("Outer clean failure", original), "Backend failed");
        Assert.IsType<RedactedException>(capture.Exception);
        Assert.DoesNotContain(secret, capture.Exception!.ToString(), StringComparison.Ordinal);

        logger.LogError(new AggregateException("Outer aggregate", new Exception("clean sibling"), original),
            "Backend failed");
        Assert.IsType<RedactedException>(capture.Exception);
        Assert.DoesNotContain(secret, capture.Exception!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void ProductionActivityCompositionRedactsEveryFreeTextField(string payload, string secret)
    {
        using var provider = new ServiceCollection().AddActivityCenter().BuildServiceProvider();
        var service = provider.GetRequiredService<IActivityCenterService>();
        var original = Event(payload);
        service.Append(original);

        var saved = Assert.Single(service.Snapshot());
        AssertSafe(saved.Title, secret);
        AssertSafe(saved.Description, secret);
        AssertSafe(saved.DiffText!, secret);
        AssertSafe(saved.ArtifactContent!, secret);
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.SessionId, saved.SessionId);
        Assert.Equal(original.RouteId, saved.RouteId);
        Assert.Equal(original.ArtifactName, saved.ArtifactName);
        Assert.Equal(original.OccurredAtUtc, saved.OccurredAtUtc);
        Assert.Equal(ActivityEventSource.Synthetic, saved.Source);
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = secret }).Items);
        Assert.Single(service.Query(new ActivityFilterCriteria { SearchQuery = "remaining" }).Items);
        AssertSafe(Assert.Single(service.QueryPage().Items).Description, secret);
        Assert.Equal(payload, original.Description);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task DurableActivityAndSearchRemainRedactedAfterFreshComposition(string payload, string secret)
    {
        await new DatabaseMigrator(_factory).MigrateAsync();
        await using (var provider = ComposeDatabase())
        {
            provider.GetRequiredService<IActivityCenterService>().Append(Event(payload));
            await provider.GetRequiredService<ActivityJournalWriteQueue>().DrainAsync();
            Assert.Equal(0, provider.GetRequiredService<ActivityJournalWriteQueue>().FailedCount);
        }

        await using (var connection = await _factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TitleRedacted || DescriptionRedacted FROM ActivityEvents;";
            Assert.DoesNotContain(secret, Assert.IsType<string>(await command.ExecuteScalarAsync()), StringComparison.Ordinal);
            command.CommandText = "SELECT body FROM ActivityEventsSearch;";
            Assert.DoesNotContain(secret, Assert.IsType<string>(await command.ExecuteScalarAsync()), StringComparison.Ordinal);
        }

        await using var reopened = ComposeDatabase();
        var service = reopened.GetRequiredService<IActivityCenterService>();
        await service.LoadAsync();
        AssertSafe(Assert.Single(service.Snapshot()).Description, secret);
        AssertSafe(Assert.Single(service.QueryPage().Items).Description, secret);
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = secret }).Items);
        Assert.Single(service.Query(new ActivityFilterCriteria { SearchQuery = "remaining" }).Items);
    }

    [Fact]
    public async Task LegacyDurableRowsOutsideMemoryWindowAreRedactedOnPublicRead()
    {
        await new DatabaseMigrator(_factory).MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        const string payload = "{\"password\":123456789,\"remaining\":7}";
        // Simulate retained legacy rows, bypassing today's ingestion contract deliberately.
        for (var index = 0; index < 5; index++)
        {
            await journal.AppendAsync(Event(payload, "legacy-" + index,
                DateTimeOffset.UtcNow.AddMinutes(index)));
        }

        await using var provider = ComposeDatabase(capacity: 2);
        var service = provider.GetRequiredService<IActivityCenterService>();
        await service.LoadAsync();
        Assert.Equal(2, service.Snapshot().Count);
        var lastPage = service.QueryPage(page: 3, pageSize: 2);
        AssertSafe(Assert.Single(lastPage.Items).Description, "123456789");
        Assert.All(service.Query(new ActivityFilterCriteria { Limit = 10 }).Items,
            item => AssertSafe(item.Description, "123456789"));
        var legacyMatches = service.Query(new ActivityFilterCriteria { SearchQuery = "123456789", Limit = 10 });
        Assert.Equal(5, legacyMatches.FilteredCount);
        Assert.All(legacyMatches.Items, item => AssertSafe(item.Description, "123456789"));
        // This read correction is not an at-rest/FTS rewrite or secure-erasure claim.
        Assert.Contains("123456789", Assert.Single(await journal.LoadNewestAsync(1)).Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CleanJsonRetainsBytesObjectIdentityAndNonSensitiveException()
    {
        const string clean = "  { \"remaining\" : 7, \"status\" : \"clean\" }  ";
        using var provider = new ServiceCollection().AddActivityCenter().BuildServiceProvider();
        var original = Event(clean);
        var service = provider.GetRequiredService<IActivityCenterService>();
        service.Append(original);
        Assert.Same(original, Assert.Single(service.Snapshot()));

        var capture = new Capture();
        var logger = new RedactingLogger(capture, new SensitiveDataFilter());
        logger.LogInformation("Backend payload {Payload}", clean);
        Assert.Equal("Backend payload " + clean, capture.Message);
        var exception = new InvalidOperationException(clean);
        logger.LogError(exception, "Failed");
        Assert.Same(exception, capture.Exception);
    }

    [Fact]
    public void DiagnosticRedactionIsIdempotentAndOrdinaryLogArgumentsKeepTheirBytes()
    {
        var filter = new SensitiveDataFilter();
        const string dirty = "{\"password\":123456789,\"remaining\":7}";
        var cleaned = filter.RedactDiagnostic(dirty);
        AssertSafe(cleaned, "123456789");
        Assert.Same(cleaned, filter.RedactDiagnostic(cleaned));
        var capture = new Capture();
        var logger = new RedactingLogger(capture, filter);
        const string ordinary = "  ordinary payload; max_tokens=7  ";
        logger.LogInformation("Backend payload {Payload}", ordinary);
        Assert.Equal("Backend payload " + ordinary, capture.Message);
        var values = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(capture.State);
        Assert.Equal(ordinary, Assert.Single(values, value => value.Key == "Payload").Value);
    }

    [Fact]
    public void MalformedJsonRetainsExistingTextPatternRedaction()
    {
        using var provider = new ServiceCollection().AddActivityCenter().BuildServiceProvider();
        var service = provider.GetRequiredService<IActivityCenterService>();
        service.AppendSystemEvent("malformed", "Fixture", "{broken password=hunter2");
        Assert.DoesNotContain("hunter2", Assert.Single(service.Snapshot()).Description, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataFilter.Placeholder, Assert.Single(service.Snapshot()).Description,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RSA ")]
    [InlineData("EC ")]
    [InlineData("OPENSSH ")]
    [InlineData("")]
    public void TruncatedPrivateKeyIsDetectedAndRedactedBeforeLogOrActivity(string kind)
    {
        var payload = "-----BEGIN " + kind + "PRIVATE KEY-----\nsynthetic-truncated-key-body";
        var filter = new SensitiveDataFilter();
        Assert.True(filter.ContainsSensitiveData(payload));
        Assert.Equal(SensitiveDataFilter.Placeholder, filter.Redact(payload));
        var capture = new Capture();
        var logger = new RedactingLogger(capture, filter);
        logger.LogWarning("Backend payload {Payload}", payload);
        Assert.DoesNotContain("synthetic-truncated-key-body", capture.Message, StringComparison.Ordinal);
        logger.LogError(new InvalidOperationException(payload), "Failed");
        Assert.IsType<RedactedException>(capture.Exception);
        Assert.DoesNotContain("synthetic-truncated-key-body", capture.Exception!.ToString(), StringComparison.Ordinal);
        using var provider = new ServiceCollection().AddActivityCenter().BuildServiceProvider();
        var service = provider.GetRequiredService<IActivityCenterService>();
        service.AppendSystemEvent("truncated", "Fixture", payload);
        Assert.Equal(SensitiveDataFilter.Placeholder, Assert.Single(service.Snapshot()).Description);
    }

    private ServiceProvider ComposeDatabase(int capacity = 10) => new ServiceCollection()
        .AddSingleton<ISqliteConnectionFactory>(_factory)
        .AddActivityCenter(capacity)
        .BuildServiceProvider();

    private static ActivityEvent Event(string payload, string id = "synthetic-event", DateTimeOffset? at = null) => new(
        id, at ?? DateTimeOffset.UtcNow, ActivityEventKind.System, ActivityRoleNames.System,
        ActivityEventState.Warning, ActivityEventSource.Synthetic, payload, payload,
        sessionId: "synthetic-session", routeId: "synthetic-route", diffText: payload,
        artifactName: "synthetic-artifact.json", artifactContent: payload);

    private static void AssertSafe(string text, string secret)
    {
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataFilter.Placeholder, text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0] : document.RootElement;
        Assert.Equal(7, root.GetProperty("remaining").GetInt32());
    }

    public void Dispose()
    {
        using var connection = _factory.CreateConnection();
        SqliteConnection.ClearPool(connection);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "llmworkgui-structured-diagnostic-tests"));
        var target = Path.GetFullPath(_directory);
        if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected fixture directory.");
        }
        Directory.Delete(target, recursive: true);
    }

    private sealed class Capture : ILogger
    {
        public string Message { get; private set; } = string.Empty;
        public object? State { get; private set; }
        public object? Scope { get; private set; }
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state;
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            State = state;
            Exception = exception;
        }
    }
}

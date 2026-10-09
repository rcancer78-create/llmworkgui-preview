using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Observability;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// One known secret planted in the stream so the run can prove the redaction boundary actually holds.
/// <para>
/// These values are synthetic fixtures invented for this driver. They are deliberately shaped like the
/// credential classes the shipped <c>SensitiveDataFilter</c> is written against, so a run that lets one
/// through has failed for exactly the reason the criterion exists - not because a fixture was too
/// unlike a real secret to be caught. They are never sent anywhere: nothing in this driver opens a
/// socket, and the values exist only inside the temporary application-data root of the run.
/// </para>
/// </summary>
internal sealed record SecretFixture(string Id, string Description, string[] RawValues)
{
    /// <summary>Searches that must return nothing for this fixture.</summary>
    public string[] SearchTerms { get; init; } = Array.Empty<string>();
}

/// <summary>The planted secret fixtures of a load run.</summary>
internal static class SecretFixtures
{
    public const string ApiKey = "sk-live-4f9a1c7b2e8d6035aa11bb22cc33dd44ee55ff660";
    public const string Password = "Hunter2Correct-Horse-Battery";
    public const string BearerToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJsbG13b3JrZ3VpIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    public const string AwsKey = "AKIAIOSFODNN7EXAMPLE";
    public const string GitHubToken = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";

    public static IReadOnlyList<SecretFixture> All { get; } = new[]
    {
        new SecretFixture(
            "assignment",
            $"api_key={ApiKey}; password={Password}",
            new[] { ApiKey, Password })
        {
            SearchTerms = new[] { ApiKey, Password, "Hunter2Correct" }
        },
        new SecretFixture(
            "bearer",
            $"Authorization: Bearer {BearerToken}",
            new[] { BearerToken })
        {
            SearchTerms = new[] { BearerToken }
        },
        new SecretFixture(
            "vendor-key",
            $"aws_access_key_id={AwsKey} github_token={GitHubToken}",
            new[] { AwsKey, GitHubToken })
        {
            SearchTerms = new[] { AwsKey, GitHubToken }
        }
    };

    /// <summary>Every raw value any fixture plants, for the byte-level journal and report scans.</summary>
    public static IReadOnlyList<string> AllRawValues { get; } = All
        .SelectMany(fixture => fixture.RawValues)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Deterministic event source of a load run.
/// <para>
/// Every event is a pure function of its sequence number, so a failing run can be reproduced exactly and
/// two runs of the same profile offer byte-identical messages. Ids are unique by construction - the id
/// embeds the sequence - which is what the normative profile requires: <c>AppendProjection</c> would
/// collapse every update of one execution onto a single <c>execution:{id}</c> row, and the run needs
/// every observation retained.
/// </para>
/// </summary>
internal sealed class ActivityLoadEventFactory
{
    private const string PayloadLine =
        "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore";

    /// <summary>Target size of an ordinary message, comfortably inside the 1 KiB small band.</summary>
    public const int SmallMessageBytes = 320;

    private readonly string[] _executionIds;
    private readonly string[] _roles =
    {
        ActivityRoleNames.Architect,
        ActivityRoleNames.TechLead,
        ActivityRoleNames.Reviewer,
        ActivityRoleNames.Coder,
        ActivityRoleNames.System
    };

    private readonly ActivityEventState[] _states =
    {
        ActivityEventState.Running,
        ActivityEventState.Completed,
        ActivityEventState.Failed,
        ActivityEventState.Cancelled,
        ActivityEventState.Warning
    };

    public ActivityLoadEventFactory(int executionIdentities)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(executionIdentities, 1);

        _executionIds = Enumerable
            .Range(1, executionIdentities)
            .Select(index => $"load-exec-{index:D2}")
            .ToArray();
    }

    /// <summary>The execution identities the run offers events for.</summary>
    public IReadOnlyList<string> ExecutionIds => _executionIds;

    /// <summary>Builds the n-th already-saved event. Always small, so the seed stays cheap to journal.</summary>
    public ActivityEvent CreateSeedEvent(long sequence, DateTimeOffset baseTimeUtc) => new(
        $"seed:{sequence:D7}",
        baseTimeUtc.AddSeconds(-(sequence % 86_400)),
        ActivityEventKind.Execution,
        _roles[(int)(sequence % _roles.Length)],
        _states[(int)(sequence % _states.Length)],
        ActivityEventSource.Native,
        $"Saved execution observation {sequence}",
        string.Create(
            CultureInfo.InvariantCulture,
            $"stage {sequence % 7} reported on {_executionIds[(int)(sequence % _executionIds.Length)]}; "
            + $"tokens {sequence % 977} reviewed against baseline {sequence % 13}."),
        sessionId: $"load-session-{sequence % 64:D2}",
        executionId: _executionIds[(int)(sequence % _executionIds.Length)],
        routeId: $"route-{sequence % 4}");

    /// <summary>
    /// Builds the n-th streamed event, honouring the documented size distribution and planting the
    /// secret fixtures at fixed, reproducible sequence numbers.
    /// </summary>
    public ActivityEvent CreateStreamEvent(
        long sequence,
        DateTimeOffset occurredAtUtc,
        ActivityMessageSizeDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);

        var executionId = _executionIds[(int)(sequence % _executionIds.Length)];
        var fixture = FixtureFor(sequence);

        var description = fixture is null
            ? BuildMessage(sequence, executionId, distribution)
            : $"{BuildMessage(sequence, executionId, distribution)} {fixture.Description}";

        return new ActivityEvent(
            $"stream:{sequence:D7}",
            occurredAtUtc,
            ActivityEventKind.Execution,
            _roles[(int)(sequence % _roles.Length)],
            _states[(int)(sequence % _states.Length)],
            ActivityEventSource.Native,
            $"Execution {executionId} event {sequence}",
            description,
            sessionId: $"load-session-{sequence % 64:D2}",
            executionId: executionId,
            routeId: $"route-{sequence % 4}");
    }

    /// <summary>The secret fixture planted at this sequence number, or null.</summary>
    public static SecretFixture? FixtureFor(long sequence) =>
        sequence switch
        {
            3 => SecretFixtures.All[0],
            11 => SecretFixtures.All[1],
            19 => SecretFixtures.All[2],
            _ => null
        };

    private static string BuildMessage(
        long sequence,
        string executionId,
        ActivityMessageSizeDistribution distribution)
    {
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"event {sequence} on {executionId} stage {sequence % 7} ");

        // The size of an over-small message comes straight from the distribution, so the documented
        // per-class size is the size actually offered rather than an approximation of it.
        var index = distribution.OverSmallIndexOf(sequence);
        var targetBytes = index < 0
            ? SmallMessageBytes
            : distribution.SizeOfLargeMessage(index);

        if (header.Length >= targetBytes)
        {
            return header;
        }

        var remaining = targetBytes - header.Length;
        var builder = new StringBuilder(targetBytes + 16);
        builder.Append(header);

        // Filler is letters and spaces only: no quote, colon, equals or brace, so the redactor has nothing
        // to rewrite and the measurement is dominated by the product rather than by the redactor.
        while (remaining > 0)
        {
            var take = Math.Min(PayloadLine.Length, remaining);
            builder.Append(PayloadLine, 0, take);
            remaining -= take;

            if (remaining > 0)
            {
                builder.Append('\n');
                remaining--;
            }
        }

        return builder.ToString();
    }
}

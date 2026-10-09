using System;
using System.Globalization;
using System.IO;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>Why a run is being performed. A short run is explicitly a smoke, never an acceptance run.</summary>
public enum ActivityLoadMode
{
    /// <summary>Short deterministic CI smoke: same code paths, tiny seed, seconds of stream.</summary>
    Smoke,

    /// <summary>The normative ТЗ §9.2 profile: 100 000 already-saved events, 50/second, 30 minutes.</summary>
    Full
}

public sealed class ActivityLoadUsageException : Exception
{
    public ActivityLoadUsageException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Parsed command line of the driver.
/// <para>
/// The two safety rails mirror the visible-workflow harness and are not optional: the run root must live
/// under the temporary directory (so a real user profile can never be the target of a load run), and the
/// screenshot directory must live under the run root (so the reference fixtures of the shipped visual
/// tests can never be overwritten by evidence of a load run).
/// </para>
/// </summary>
internal sealed record ActivityLoadOptions
{
    public const string SmokeMode = "smoke";
    public const string FullMode = "full";

    private const int SmokeSeedEvents = 2_000;
    private const int SmokeDurationSeconds = 20;

    public required string RunRoot { get; init; }

    public required ActivityLoadMode Mode { get; init; }

    public required string ScreenshotDirectory { get; init; }

    public required ActivityLoadProfile Profile { get; init; }

    public required ActivityMessageSizeDistribution SizeDistribution { get; init; }

    public required int JournalRetentionLimit { get; init; }

    public required int HoldSeconds { get; init; }

    public required bool SkipNegativeControl { get; init; }

    /// <summary>
    /// Seed-count and duration overrides for diagnosis. They exist so a suspected hang can be isolated to
    /// a handful of events under an enforced child timeout, instead of re-running a 2 000-event or a
    /// 90 000-event profile and waiting minutes for the same stall. A run using them is still labelled with
    /// the profile it actually ran, so a reduced run can never be reported as the normative one.
    /// </summary>
    public required bool IsReduced { get; init; }

    public bool IsSmoke => Mode == ActivityLoadMode.Smoke;

    public string ModeDisplay => Mode == ActivityLoadMode.Smoke ? "smoke" : "full";

    public static string Usage =>
        "Usage: LLMWorkGUI.ActivityLoadDriver [--mode smoke|full] [--run-root <temp dir>] "
        + "[--screenshot-dir <dir under run root>] [--journal-retention N] [--hold-seconds N] "
        + "[--seed N] [--stream-seconds N] [--skip-negative-control]";

    public static ActivityLoadOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = ActivityLoadMode.Smoke;
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "llm-workflow-runs",
            "LLMWorkGUI",
            $"phase11-activity-load-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        string? screenshotDirectory = null;
        var journalRetentionLimit = ActivityJournalOptions.DefaultRetentionLimit;
        var holdSeconds = 0;
        var skipNegativeControl = false;
        var seedOverride = -1;
        var streamSecondsOverride = -1d;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "--mode":
                    mode = ParseMode(Next(args, ref index, argument));
                    break;

                case "--run-root":
                    runRoot = Next(args, ref index, argument);
                    break;

                case "--screenshot-dir":
                    screenshotDirectory = Next(args, ref index, argument);
                    break;

                case "--journal-retention":
                    journalRetentionLimit = int.Parse(
                        Next(args, ref index, argument),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    break;

                case "--hold-seconds":
                    holdSeconds = int.Parse(
                        Next(args, ref index, argument),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    break;

                case "--skip-negative-control":
                    skipNegativeControl = true;
                    break;

                case "--seed":
                    seedOverride = int.Parse(
                        Next(args, ref index, argument),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                    break;

                case "--stream-seconds":
                    streamSecondsOverride = double.Parse(
                        Next(args, ref index, argument),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture);
                    break;

                case "--help":
                case "-h":
                    throw new ActivityLoadUsageException(Usage);

                default:
                    throw new ActivityLoadUsageException(
                        $"Unrecognised argument '{argument}'.{Environment.NewLine}{Usage}");
            }
        }

        runRoot = Path.GetFullPath(runRoot);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());

        if (!IsUnder(tempRoot, runRoot))
        {
            throw new ActivityLoadUsageException(
                $"The run root must live under the temporary directory '{tempRoot}'; '{runRoot}' does not. "
                + "A load run writes hundreds of megabytes and must never target a real profile.");
        }

        screenshotDirectory ??= Path.Combine(runRoot, "screenshots");
        screenshotDirectory = Path.GetFullPath(screenshotDirectory);

        if (!IsUnder(runRoot, screenshotDirectory))
        {
            throw new ActivityLoadUsageException(
                $"The screenshot directory must live under the run root '{runRoot}'; "
                + $"'{screenshotDirectory}' does not.");
        }

        try
        {
            TemporaryEvidencePath.ValidateDescendant(runRoot, tempRoot);
            TemporaryEvidencePath.ValidateDescendant(screenshotDirectory, runRoot);
        }
        catch (ArgumentException exception) { throw new ActivityLoadUsageException(exception.Message); }

        var profile = mode == ActivityLoadMode.Full
            ? new ActivityLoadProfile()
            : new ActivityLoadProfile(
                seedEvents: SmokeSeedEvents,
                duration: TimeSpan.FromSeconds(SmokeDurationSeconds));

        var isReduced = false;

        if (seedOverride > 0)
        {
            profile = new ActivityLoadProfile(
                seedEvents: seedOverride,
                eventsPerSecond: profile.EventsPerSecond,
                duration: streamSecondsOverride > 0
                    ? TimeSpan.FromSeconds(streamSecondsOverride)
                    : profile.Duration,
                executionIdentities: profile.ExecutionIdentities);
            isReduced = true;
        }
        else if (streamSecondsOverride > 0)
        {
            profile = new ActivityLoadProfile(
                seedEvents: profile.SeedEvents,
                eventsPerSecond: profile.EventsPerSecond,
                duration: TimeSpan.FromSeconds(streamSecondsOverride),
                executionIdentities: profile.ExecutionIdentities);
            isReduced = true;
        }

        return new ActivityLoadOptions
        {
            RunRoot = runRoot,
            Mode = mode,
            ScreenshotDirectory = screenshotDirectory,
            Profile = profile,
            SizeDistribution = ActivityMessageSizeDistribution.ForStream(profile.OfferedEvents),
            JournalRetentionLimit = journalRetentionLimit,
            HoldSeconds = holdSeconds,
            SkipNegativeControl = skipNegativeControl,
            IsReduced = isReduced
        };
    }

    private static ActivityLoadMode ParseMode(string value) => value switch
    {
        SmokeMode => ActivityLoadMode.Smoke,
        FullMode => ActivityLoadMode.Full,
        _ => throw new ActivityLoadUsageException(
            $"Unknown mode '{value}'. Use '{SmokeMode}' or '{FullMode}'.{Environment.NewLine}{Usage}")
    };

    private static string Next(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length)
        {
            throw new ActivityLoadUsageException($"{argument} requires a value.{Environment.NewLine}{Usage}");
        }

        return args[++index];
    }

    private static bool IsUnder(string root, string candidate)
    {
        // Path.GetTempPath() ends in a separator, so both sides are normalized before the comparison;
        // otherwise every legitimate run root looks like a sibling of the temp directory.
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}

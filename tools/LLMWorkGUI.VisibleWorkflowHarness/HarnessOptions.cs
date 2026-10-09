using System;
using System.IO;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// Command line and environment of one visible acceptance run.
/// <para>
/// The run root defaults to a fresh directory under <c>%TEMP%</c>. Nothing is ever written outside it: the
/// harness refuses to start when the caller points it at a directory that is not under the temp root, so an
/// acceptance run cannot touch a real application-data directory.
/// </para>
/// </summary>
internal sealed record HarnessOptions
{
    public const string DefaultMode = "visible";
    public const string CiMode = "ci";

    private const int DefaultVisibleHoldSeconds = 1800;
    private const int DefaultCiHoldSeconds = 0;

    /// <summary>Directory that holds the temporary app data, the evidence files and the report.</summary>
    public required string RunRoot { get; init; }

    /// <summary><c>visible</c> holds the window open for a human; <c>ci</c> closes it automatically.</summary>
    public required string Mode { get; init; }

    /// <summary>How long the visible mode keeps the window open before it closes itself.</summary>
    public required TimeSpan HoldTimeout { get; init; }

    /// <summary>Directory the rendered screenshots are written to.</summary>
    public required string ScreenshotDirectory { get; init; }

    public bool IsVisibleMode => string.Equals(Mode, DefaultMode, StringComparison.OrdinalIgnoreCase);
    public bool AllUi { get; init; }

    public static string Usage =>
        "Usage: LLMWorkGUI.VisibleWorkflowHarness [--mode visible|ci] [--hold-seconds N] "
        + "[--run-root <temp dir>] [--screenshot-dir <dir under run root>] [--all-ui]";

    public static HarnessOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = DefaultMode;
        var holdSeconds = -1;
        var allUi = false;
        string? screenshotDirectory = null;
        string? runRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "--all-ui":
                    allUi = true;
                    break;
                case "--mode":
                    mode = ReadValue(args, ref index, argument);
                    break;
                case "--hold-seconds":
                    holdSeconds = int.Parse(ReadValue(args, ref index, argument));
                    break;
                case "--screenshot-dir":
                    screenshotDirectory = ReadValue(args, ref index, argument);
                    break;
                case "--run-root":
                    runRoot = ReadValue(args, ref index, argument);
                    break;
                case "--help":
                case "-h":
                    throw new HarnessUsageException(Usage);
                default:
                    throw new HarnessUsageException($"Unknown argument '{argument}'.{Environment.NewLine}{Usage}");
            }
        }

        if (!string.Equals(mode, DefaultMode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, CiMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessUsageException(
                $"--mode must be '{DefaultMode}' or '{CiMode}', not '{mode}'.{Environment.NewLine}{Usage}");
        }

        runRoot ??= Path.Combine(
            Path.GetTempPath(),
            "llm-workflow-runs",
            "LLMWorkGUI",
            $"phase10-visible-acceptance-{DateTime.Now:yyyyMMdd-HHmmss}");

        runRoot = Path.GetFullPath(runRoot);

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!IsUnderDirectory(runRoot, tempRoot))
        {
            throw new HarnessUsageException(
                $"The run root must live under the temporary directory ({tempRoot}), but it is '{runRoot}'. "
                + "The harness never writes outside a temporary root.");
        }

        var isVisible = string.Equals(mode, DefaultMode, StringComparison.OrdinalIgnoreCase);
        var effectiveHoldSeconds = holdSeconds >= 0
            ? holdSeconds
            : (isVisible ? DefaultVisibleHoldSeconds : DefaultCiHoldSeconds);

        var resolvedScreenshots = Path.GetFullPath(screenshotDirectory
            ?? Environment.GetEnvironmentVariable("LLMWORKGUI_SCREENSHOT_DIR")
            ?? Path.Combine(runRoot, "screenshots"));

        if (!IsUnderDirectory(resolvedScreenshots, runRoot))
        {
            throw new HarnessUsageException(
                $"The screenshot directory must live under the run root ({runRoot}), but it is "
                + $"'{resolvedScreenshots}'. Reference screenshot fixtures are never a target of this harness.");
        }

        try
        {
            TemporaryEvidencePath.ValidateDescendant(runRoot, tempRoot);
            TemporaryEvidencePath.ValidateDescendant(resolvedScreenshots, runRoot);
        }
        catch (ArgumentException exception) { throw new HarnessUsageException(exception.Message); }

        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(resolvedScreenshots);

        return new HarnessOptions
        {
            RunRoot = runRoot,
            AllUi = allUi,
            Mode = mode,
            HoldTimeout = TimeSpan.FromSeconds(effectiveHoldSeconds),
            ScreenshotDirectory = resolvedScreenshots
        };
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length)
        {
            throw new HarnessUsageException($"'{argument}' requires a value.{Environment.NewLine}{Usage}");
        }

        index++;
        return args[index];
    }

    private static bool IsUnderDirectory(string candidate, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A bad command line: reported on the console, never as a partial acceptance run.</summary>
internal sealed class HarnessUsageException : Exception
{
    public HarnessUsageException(string message)
        : base(message)
    {
    }
}

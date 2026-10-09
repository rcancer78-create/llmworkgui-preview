using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

/// <summary>
/// Writes a rendered screenshot to disk.
///
/// The screenshot directory is a shared, long-lived folder inside the checkout, so the files in it can
/// briefly be held by another agent on the machine (a file indexer, an antivirus scanner, an editor
/// preview, or a previous test host that has not yet released its handle). Creating the file with a
/// single unguarded <see cref="File.Create(string)"/> therefore fails intermittently with
/// <see cref="IOException"/>, which turns an unrelated capture test red and makes the suite unusable as
/// a release gate.
///
/// The write is retried with a growing backoff. Only a genuinely persistent failure is surfaced, and it
/// is reported with the path and the underlying error so the cause is diagnosable.
/// </summary>
internal static class ScreenshotFile
{
    private const int MaxAttempts = 10;

    public static string OutputDirectory
    {
        get
        {
            var customDir = Environment.GetEnvironmentVariable("LLMWORKGUI_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(customDir))
            {
                Directory.CreateDirectory(customDir);
                return customDir;
            }

            var updateRef = Environment.GetEnvironmentVariable("LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS");
            if (string.Equals(updateRef, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(updateRef, "true", StringComparison.OrdinalIgnoreCase))
            {
                var refDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Screenshots"));
                Directory.CreateDirectory(refDir);
                return refDir;
            }

            var defaultDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "Screenshots"));
            Directory.CreateDirectory(defaultDir);
            return defaultDir;
        }
    }

    public static void Save(BitmapEncoder encoder, string path)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var stream = File.Create(path);
                encoder.Save(stream);
                return;
            }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        throw new IOException(
            $"The screenshot '{path}' could not be written after {MaxAttempts} attempts because the file " +
            "stayed locked by another process.");
    }
}

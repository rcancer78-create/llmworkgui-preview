using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// Immediately-flushed phase trace of a run.
/// <para>
/// The trace is appended and flushed on every boundary rather than accumulated and written at the end,
/// because a load run is long and mostly unattended. A buffered in-memory trace is worthless exactly when
/// it is needed: a run that hangs, is killed, or dies on a native fault never reaches the code that would
/// have written it. Each line reaching the file immediately is what makes a stall diagnosable from the
/// run root alone.
/// </para>
/// </summary>
internal sealed class DriverTrace : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly StopwatchClock _clock = new();

    public DriverTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    /// <summary>Appends one boundary marker and flushes it to disk before returning.</summary>
    public void Step(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{_clock.Elapsed.TotalSeconds,9:N3}s] {message}");

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }

        Console.Error.WriteLine(line);
    }

    /// <summary>Appends a boundary marker together with a measurement.</summary>
    public void Step(string message, long value, string unit)
    {
        Step(string.Create(
            CultureInfo.InvariantCulture,
            $"{message}: {value:N0} {unit}"));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private sealed class StopwatchClock
    {
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;
    }
}

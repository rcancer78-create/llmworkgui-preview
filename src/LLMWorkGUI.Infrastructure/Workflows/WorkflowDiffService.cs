using System.Text;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Computes a file-level unified diff between a pristine source directory and a candidate scratch
/// directory. Paths are compared ordinally with '/' separators; text diffs use a standard LCS line
/// script with three context lines, and binary files are reported without line statistics.
/// </summary>
public sealed class WorkflowDiffService : IWorkflowDiffService
{
    public const int MaxTextLinesPerFile = 100_000;
    public const int MaxComparisonReadBytes = 64 * 1024 * 1024;
    public const int MaxRenderedDiffBytes = 8 * 1024 * 1024;
    private const int MaxRenderedDiffCharacters = 4 * 1024 * 1024;
    private const int ContextLines = 3;
    private const int BinarySniffBytes = 8192;
    private const long MaxLcsCells = 4_000_000;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];

    public async Task<WorkflowPackageDiff> CompareAsync(
        string sourceDirectory,
        string candidateDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateDirectory);

        var sourceFiles = IndexFiles(sourceDirectory);
        var candidateFiles = IndexFiles(candidateDirectory);

        var paths = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in sourceFiles.Keys)
        {
            paths.Add(path);
        }

        foreach (var path in candidateFiles.Keys)
        {
            paths.Add(path);
        }

        var diffs = new List<WorkflowFileDiff>(paths.Count);
        var remainingReadBytes = MaxComparisonReadBytes;
        var renderedBytes = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceBytes = sourceFiles.TryGetValue(path, out var sourcePath)
                ? await ReadBoundedFileAsync(sourcePath, remainingReadBytes, cancellationToken).ConfigureAwait(false)
                : null;
            remainingReadBytes -= sourceBytes?.Length ?? 0;

            var candidateBytes = candidateFiles.TryGetValue(path, out var candidatePath)
                ? await ReadBoundedFileAsync(candidatePath, remainingReadBytes, cancellationToken).ConfigureAwait(false)
                : null;
            remainingReadBytes -= candidateBytes?.Length ?? 0;

            var diff = CompareFile(path, sourceBytes, candidateBytes);
            renderedBytes += Encoding.UTF8.GetByteCount(diff.UnifiedDiffText);
            if (renderedBytes > MaxRenderedDiffBytes)
                throw new InvalidDataException("Workflow comparison exceeds its rendered diff byte budget.");
            diffs.Add(diff);
        }

        return new WorkflowPackageDiff(diffs);
    }

    private static WorkflowFileDiff CompareFile(
        string relativePath,
        byte[]? sourceBytes,
        byte[]? candidateBytes)
    {
        var sourceExists = sourceBytes is not null;
        var candidateExists = candidateBytes is not null;
        var kind = !sourceExists
            ? WorkflowFileDiffKind.Added
            : !candidateExists
                ? WorkflowFileDiffKind.Deleted
                : WorkflowFileDiffKind.Modified;

        if (sourceExists && candidateExists
            && sourceBytes!.AsSpan().SequenceEqual(candidateBytes!.AsSpan()))
        {
            return new WorkflowFileDiff(
                relativePath,
                WorkflowFileDiffKind.Unchanged,
                0,
                0,
                string.Empty,
                isBinary: false);
        }

        var sourceText = string.Empty;
        var candidateText = string.Empty;
        var sourceIsText = sourceExists && TryDecodeText(sourceBytes!, out sourceText);
        var candidateIsText = candidateExists && TryDecodeText(candidateBytes!, out candidateText);
        var isBinary = (sourceExists && !sourceIsText) || (candidateExists && !candidateIsText);

        if (isBinary)
        {
            return new WorkflowFileDiff(relativePath, kind, 0, 0, string.Empty, isBinary: true);
        }

        var before = sourceExists ? SplitLines(sourceText) : Array.Empty<string>();
        var after = candidateExists ? SplitLines(candidateText) : Array.Empty<string>();
        var (script, linesAdded, linesDeleted) = BuildLineScript(before, after);
        var unifiedDiff = BuildUnifiedDiff(relativePath, script);
        if (kind == WorkflowFileDiffKind.Modified && linesAdded == 0 && linesDeleted == 0)
        {
            // Raw identity changed although decoding/line splitting removes its representation.
            // Report that observation explicitly; do not invent added/deleted text or binary data.
            unifiedDiff = $"--- a/{relativePath}\n+++ b/{relativePath}\n"
                + "# Raw bytes changed; decoded text lines are identical. Encoding, BOM or line endings may differ.\n";
        }

        return new WorkflowFileDiff(
            relativePath,
            kind,
            linesAdded,
            linesDeleted,
            unifiedDiff,
            isBinary: false);
    }

    private static Dictionary<string, string> IndexFiles(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
        {
            return files;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        foreach (var fullPath in Directory.EnumerateFiles(directory, "*", options))
        {
            if (files.Count == WorkflowImportLimits.MaxFileCount)
                throw new InvalidDataException("Workflow comparison exceeds its file-count budget.");
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(directory, fullPath));
            files[relativePath] = fullPath;
        }

        return files;
    }

    private static (List<DiffLine> Script, int LinesAdded, int LinesDeleted) BuildLineScript(
        string[] before,
        string[] after)
    {
        var script = new List<DiffLine>(before.Length + after.Length);
        var linesAdded = 0;
        var linesDeleted = 0;

        if (before.Length == 0 || after.Length == 0 || (long)before.Length * after.Length > MaxLcsCells)
        {
            foreach (var line in before)
            {
                script.Add(new DiffLine(DiffOperation.Delete, line));
                linesDeleted++;
            }

            foreach (var line in after)
            {
                script.Add(new DiffLine(DiffOperation.Insert, line));
                linesAdded++;
            }

            return (script, linesAdded, linesDeleted);
        }

        var lengths = new int[before.Length + 1, after.Length + 1];

        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;

        while (x < before.Length && y < after.Length)
        {
            if (string.Equals(before[x], after[y], StringComparison.Ordinal))
            {
                script.Add(new DiffLine(DiffOperation.Equal, before[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                script.Add(new DiffLine(DiffOperation.Delete, before[x]));
                x++;
                linesDeleted++;
            }
            else
            {
                script.Add(new DiffLine(DiffOperation.Insert, after[y]));
                y++;
                linesAdded++;
            }
        }

        while (x < before.Length)
        {
            script.Add(new DiffLine(DiffOperation.Delete, before[x]));
            x++;
            linesDeleted++;
        }

        while (y < after.Length)
        {
            script.Add(new DiffLine(DiffOperation.Insert, after[y]));
            y++;
            linesAdded++;
        }

        return (script, linesAdded, linesDeleted);
    }

    private static string BuildUnifiedDiff(string relativePath, List<DiffLine> script)
    {
        var changeIndexes = new List<int>();

        for (var i = 0; i < script.Count; i++)
        {
            if (script[i].Operation != DiffOperation.Equal)
            {
                changeIndexes.Add(i);
            }
        }

        if (changeIndexes.Count == 0)
        {
            return string.Empty;
        }

        var hunks = new List<(int Start, int End)>();
        var first = changeIndexes[0];
        var hunkStart = Math.Max(0, first - ContextLines);
        var hunkEnd = Math.Min(script.Count, first + ContextLines + 1);

        foreach (var index in changeIndexes.Skip(1))
        {
            if (index - ContextLines <= hunkEnd)
            {
                hunkEnd = Math.Min(script.Count, index + ContextLines + 1);
            }
            else
            {
                hunks.Add((hunkStart, hunkEnd));
                hunkStart = Math.Max(0, index - ContextLines);
                hunkEnd = Math.Min(script.Count, index + ContextLines + 1);
            }
        }

        hunks.Add((hunkStart, hunkEnd));

        var builder = new StringBuilder();
        builder.Append("--- a/").Append(relativePath).Append('\n');
        builder.Append("+++ b/").Append(relativePath).Append('\n');

        foreach (var (start, end) in hunks)
        {
            var oldCount = 0;
            var newCount = 0;

            for (var i = start; i < end; i++)
            {
                if (script[i].Operation != DiffOperation.Insert)
                {
                    oldCount++;
                }

                if (script[i].Operation != DiffOperation.Delete)
                {
                    newCount++;
                }
            }

            var oldLinesBefore = CountLinesBefore(script, start, beforeSide: true);
            var newLinesBefore = CountLinesBefore(script, start, beforeSide: false);
            var oldStart = oldCount == 0 ? oldLinesBefore : oldLinesBefore + 1;
            var newStart = newCount == 0 ? newLinesBefore : newLinesBefore + 1;

            builder
                .Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                .Append(" +").Append(newStart).Append(',').Append(newCount)
                .Append(" @@\n");

            for (var i = start; i < end; i++)
            {
                if ((long)builder.Length + script[i].Text.Length + 2 > MaxRenderedDiffCharacters)
                    throw new InvalidDataException("Workflow comparison exceeds its rendered diff text budget.");
                var prefix = script[i].Operation switch
                {
                    DiffOperation.Delete => '-',
                    DiffOperation.Insert => '+',
                    _ => ' '
                };

                builder.Append(prefix).Append(script[i].Text).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static int CountLinesBefore(List<DiffLine> script, int index, bool beforeSide)
    {
        var count = 0;

        for (var i = 0; i < index; i++)
        {
            if (beforeSide
                ? script[i].Operation != DiffOperation.Insert
                : script[i].Operation != DiffOperation.Delete)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = string.Empty;

        if (bytes.Length == 0)
        {
            return true;
        }

        var span = bytes.AsSpan();

        if (span.StartsWith(Utf8Preamble))
        {
            return TryDecodeUtf8(span[Utf8Preamble.Length..], out text);
        }

        if (span.StartsWith(Utf16LittleEndianPreamble))
        {
            text = Encoding.Unicode.GetString(span[Utf16LittleEndianPreamble.Length..]);
            return true;
        }

        if (span.StartsWith(Utf16BigEndianPreamble))
        {
            text = Encoding.BigEndianUnicode.GetString(span[Utf16BigEndianPreamble.Length..]);
            return true;
        }

        if (span[..Math.Min(span.Length, BinarySniffBytes)].IndexOf((byte)0) >= 0)
        {
            return false;
        }

        return TryDecodeUtf8(span, out text);
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string[] SplitLines(string text)
    {
        var lineCount = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                lineCount++;
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') lineCount++;
            if (lineCount > MaxTextLinesPerFile)
                throw new InvalidDataException("Workflow text diff exceeds its line budget.");
        }
        if (text.Length != 0 && text[^1] is not ('\r' or '\n') && ++lineCount > MaxTextLinesPerFile)
            throw new InvalidDataException("Workflow text diff exceeds its line budget.");
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');

        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        return lines;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int remainingBytes, CancellationToken token)
    {
        var limit = Math.Min(WorkflowImportLimits.MaxSingleFileBytes, remainingBytes);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > limit)
            throw new InvalidDataException("Workflow comparison exceeds its file or total read budget.");
        using var result = new MemoryStream((int)source.Length);
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, limit - result.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > limit)
                throw new InvalidDataException("Workflow comparison exceeds its file or total read budget.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private enum DiffOperation
    {
        Equal,
        Delete,
        Insert
    }

    private readonly record struct DiffLine(DiffOperation Operation, string Text);
}

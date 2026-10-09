using System.Text;
using System.Text.RegularExpressions;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Static pre-send secret scanner. It reuses <see cref="SensitiveDataFilter"/> patterns for
/// detection and redaction, never returns raw secret material in findings, and recommends every
/// matching file for exclusion from the adaptation payload (ТЗ §6.14).
/// </summary>
public sealed partial class WorkflowSecretScanner : IWorkflowSecretScanner
{
    private const int MaxSnippetLength = 200;
    private const int MaxLineScanLength = 4096;

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);

    private readonly SensitiveDataFilter _sensitiveDataFilter;

    public WorkflowSecretScanner(SensitiveDataFilter? sensitiveDataFilter = null)
    {
        _sensitiveDataFilter = sensitiveDataFilter ?? new SensitiveDataFilter();
    }

    public async Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
        ScratchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        if (workspace.IsCleanedUp)
        {
            throw new InvalidOperationException("Scratch workspace has already been cleaned up.");
        }

        var findings = new List<WorkflowSecretFinding>();
        var exclusions = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var fileCount = 0;
        var directoryCount = 1;
        try
        {
            var pending = new Stack<string>();
            pending.Push(workspace.DirectoryPath);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                InputSanitizer.EnsureNoReparsePoints(workspace.DirectoryPath, directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InputSanitizer.EnsureNoReparsePoints(workspace.DirectoryPath, path);
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    {
                        if (++directoryCount > WorkflowImportLimits.MaxFileCount)
                            throw new WorkflowValidationException("Secret scan exceeds the allowed directory count.");
                        pending.Push(path);
                        continue;
                    }
                    if (++fileCount > WorkflowImportLimits.MaxFileCount)
                        throw new WorkflowValidationException("Secret scan exceeds the allowed file count.");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length > WorkflowImportLimits.MaxSingleFileBytes
                        || (totalBytes += stream.Length) > WorkflowImportLimits.MaxUncompressedTotalBytes)
                        throw new WorkflowValidationException("Secret scan exceeds the allowed file size.");
                    var bytes = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    var relative = NormalizeRelativePath(Path.GetRelativePath(workspace.DirectoryPath, path));
                    var report = Scan(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [relative] = bytes }, cancellationToken);
                    findings.AddRange(report.Findings);
                    if (findings.Count > WorkflowImportLimits.MaxFileCount)
                        throw new WorkflowValidationException("Secret scan exceeds the allowed finding count.");
                    exclusions.UnionWith(report.RecommendedExcludedFiles);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PathTraversalException)
        {
            throw new WorkflowValidationException("Part of the scratch workspace could not be safely scanned.", exception);
        }
        var ordered = findings.OrderBy(finding => finding.RelativePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.LineNumber).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new WorkflowSecretScanReport(ordered.Length > 0, ordered,
            exclusions.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    public Task<WorkflowSecretScanReport> ScanFilesAsync(
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();

        if (files.Count > WorkflowImportLimits.MaxFileCount)
            throw new WorkflowValidationException("Secret scan exceeds the allowed file count.");
        long totalBytes = 0;
        var normalizedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pair.Value is null)
            {
                throw new ArgumentException("File contents must not be null.", nameof(files));
            }

            if (pair.Value.Length > WorkflowImportLimits.MaxSingleFileBytes
                || (totalBytes += pair.Value.Length) > WorkflowImportLimits.MaxUncompressedTotalBytes)
                throw new WorkflowValidationException("Secret scan exceeds the allowed file size.");
            if (!normalizedFiles.TryAdd(NormalizeRelativePath(pair.Key), pair.Value))
                throw new WorkflowValidationException("Secret scan file paths collide.");
        }

        return Task.FromResult(Scan(normalizedFiles, cancellationToken));
    }

    private WorkflowSecretScanReport Scan(Dictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var findings = new List<WorkflowSecretFinding>();
        var excludedFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = DecodeText(pair.Value);
            if (text is null)
            {
                // NUL-bearing data whose text encoding cannot be established is not a clean scan.
                // Exclude its body conservatively without exposing raw bytes in evidence.
                findings.Add(new WorkflowSecretFinding(pair.Key, 1, "UnsupportedEncoding", SensitiveDataFilter.Placeholder));
                excludedFiles.Add(pair.Key);
                if (findings.Count > WorkflowImportLimits.MaxFileCount)
                    throw new WorkflowValidationException("Secret scan exceeds the allowed finding count.");
                continue;
            }
            var lineNumber = 0;

            foreach (var line in EnumerateLines(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;

                string? ruleName;
                try { ruleName = MatchRule(pair.Key, line, cancellationToken); }
                catch (RegexMatchTimeoutException exception)
                {
                    throw new WorkflowValidationException("Secret scan exceeded its verification budget.", exception);
                }

                if (ruleName is null)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                findings.Add(new WorkflowSecretFinding(
                    pair.Key,
                    lineNumber,
                    ruleName,
                    RedactSnippet(line)));

                excludedFiles.Add(pair.Key);
                if (findings.Count > WorkflowImportLimits.MaxFileCount)
                    throw new WorkflowValidationException("Secret scan exceeds the allowed finding count.");
            }
        }

        var orderedExclusions = excludedFiles
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        return new WorkflowSecretScanReport(
            findings.Count > 0,
            findings,
            orderedExclusions);
    }

    private string? MatchRule(string relativePath, string line, CancellationToken cancellationToken)
    {
        if (line.Length == 0)
        {
            return null;
        }

        if (Matches(PrivateKeyHeaderRegex()))
        {
            return "PrivateKey";
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (line.Contains("urn:llmworkgui:secret:", StringComparison.Ordinal))
        {
            return "SecretReference";
        }

        if (Matches(BearerTokenRegex()))
        {
            return "BearerToken";
        }

        if (Matches(OpenAiKeyRegex()))
        {
            return "OpenAiKey";
        }

        if (Matches(AwsAccessKeyRegex()))
        {
            return "AwsAccessKey";
        }

        if (Matches(GitHubTokenRegex()))
        {
            return "GitHubToken";
        }

        if (Matches(SlackTokenRegex()))
        {
            return "SlackToken";
        }

        if (Matches(GoogleApiKeyRegex()))
        {
            return "GoogleApiKey";
        }

        if (HasSensitiveAssignment(line, cancellationToken))
        {
            return "SensitiveAssignment";
        }

        // Generated .deps.json files contain package ids as JSON property names. A package id such as
        // Microsoft.Extensions.Configuration.UserSecrets is dependency metadata, not a credential or a
        // secret value. Keep the broad policy for every other file and every non-version value here, but
        // do not turn a harmless package-name row into an excluded workflow file.
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDependencyNameMetadata(relativePath, line))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sensitive = _sensitiveDataFilter.ContainsSensitiveData(line);
        cancellationToken.ThrowIfCancellationRequested();
        return sensitive ? "SensitiveData" : null;

        bool Matches(Regex regex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = regex.IsMatch(line);
            cancellationToken.ThrowIfCancellationRequested();
            return matches;
        }
    }

    private static bool IsDependencyNameMetadata(string relativePath, string line) =>
        relativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
        && DependencyVersionMetadataRegex().IsMatch(line);

    private static bool HasSensitiveAssignment(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (Match match in AssignmentRegex().Matches(line))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SensitiveDataFilter.IsSensitiveName(match.Groups["name"].Value))
            {
                return true;
            }
        }

        return false;
    }

    private string RedactSnippet(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.Length > MaxLineScanLength)
        {
            trimmed = trimmed[..MaxLineScanLength];
        }

        var redacted = _sensitiveDataFilter.Redact(trimmed);
        redacted = SecretReferenceRegex().Replace(redacted, SensitiveDataFilter.Placeholder);

        if (string.Equals(redacted, trimmed, StringComparison.Ordinal))
        {
            redacted = SensitiveDataFilter.Placeholder;
        }

        return redacted.Length <= MaxSnippetLength ? redacted : redacted[..MaxSnippetLength];
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);

        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static string? DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var span = bytes.AsSpan();

        if (span.StartsWith(Utf8Preamble))
        {
            return Encoding.UTF8.GetString(span[Utf8Preamble.Length..]);
        }

        if (span.StartsWith(Utf16LittleEndianPreamble))
        {
            return Encoding.Unicode.GetString(span[Utf16LittleEndianPreamble.Length..]);
        }

        if (span.StartsWith(Utf16BigEndianPreamble))
        {
            return Encoding.BigEndianUnicode.GetString(span[Utf16BigEndianPreamble.Length..]);
        }

        if (span.IndexOf((byte)0) >= 0)
        {
            if ((span.Length & 1) != 0) return null;
            var evenZeros = 0;
            var oddZeros = 0;
            for (var index = 0; index < span.Length; index += 2)
            {
                if (span[index] == 0) evenZeros++;
                if (span[index + 1] == 0) oddZeros++;
            }
            // ASCII token characters establish one byte order; ambiguous binary/NUL payloads are
            // excluded instead of being interpreted as safe UTF-8 and silently losing secret names.
            try
            {
                if ((long)oddZeros > (long)evenZeros * 4) return StrictUtf16LittleEndian.GetString(span);
                if ((long)evenZeros > (long)oddZeros * 4) return StrictUtf16BigEndian.GetString(span);
            }
            catch (DecoderFallbackException) { return null; }
            return null;
        }

        return Encoding.UTF8.GetString(span);
    }

    private static string NormalizeRelativePath(string path)
    {
        try
        {
            return InputSanitizer.NormalizeArchiveEntryPath(path);
        }
        catch (PathTraversalException exception)
        {
            throw new WorkflowValidationException("Secret scan file path is not allowed.", exception);
        }
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex PrivateKeyHeaderRegex();

    [GeneratedRegex(@"urn:llmworkgui:secret:[^\s""']*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex SecretReferenceRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_.\-]+)(?<sep>\s*[:=]\s*)(?:""(?<quoted>[^""\r\n]*)""|(?<value>[^\s&;,""'<>\[\]?]{4,}))", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(
        @"^\s*""[^""\r\n]*UserSecrets[^""\r\n]*""\s*:\s*""\d+(?:\.\d+){1,3}(?:[-+][^""\r\n]+)?""\s*,?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex DependencyVersionMetadataRegex();
}

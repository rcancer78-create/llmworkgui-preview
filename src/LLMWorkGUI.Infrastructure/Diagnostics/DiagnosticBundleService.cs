using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Diagnostics;

/// <summary>
/// Redacted diagnostic bundle generator. Every collected text passes through
/// <see cref="SensitiveDataFilter"/> before it enters the archive, and the whole candidate set is
/// scanned with <see cref="IWorkflowSecretScanner"/> before export: a finding that survived redaction
/// blocks the archive instead of leaking it (ТЗ §9.3, ROADMAP Phase 12).
/// </summary>
public sealed class DiagnosticBundleService : IDiagnosticBundleService
{
    public const string SecretReferenceRule = "SecretReference";

    private const int HealthAuditLimit = 200;
    private const string ManifestRelativePath = "manifest.json";
    private const string BundleReadmeRelativePath = "bundle-readme.txt";
    private const string EnvironmentRelativePath = "environment.json";
    private const string ConfigurationRelativePath = "storage-configuration.json";
    private const string CliStatusRelativePath = "cli-status.json";
    private const string DatabaseSchemaRelativePath = "database/schema.json";
    private const string HealthAuditRelativePath = "database/health-audit.json";
    private const string LogsPrefix = "logs";
    private const string RunsPrefix = "runs";

    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> TextLogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log",
        ".txt",
        ".json",
        ".jsonl",
        ".ndjson",
        ".out",
        ".err",
        ".md",
        ".trace"
    };

    private static readonly string[] RunLogFileNames = { "stdout.log", "stderr.log" };

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];

    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IWorkflowSecretScanner _secretScanner;
    private readonly SensitiveDataFilter _sensitiveDataFilter;
    private readonly StorageOptions _storageOptions;
    private readonly IHealthEventRepository? _healthEventRepository;
    private readonly ICliDetectionService? _cliDetectionService;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, CachedBundle> _cache = new(StringComparer.Ordinal);

    public DiagnosticBundleService(
        ISqliteConnectionFactory connectionFactory,
        IWorkflowSecretScanner secretScanner,
        SensitiveDataFilter sensitiveDataFilter,
        StorageOptions storageOptions,
        IHealthEventRepository? healthEventRepository = null,
        ICliDetectionService? cliDetectionService = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(secretScanner);
        ArgumentNullException.ThrowIfNull(sensitiveDataFilter);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _connectionFactory = connectionFactory;
        _secretScanner = secretScanner;
        _sensitiveDataFilter = sensitiveDataFilter;
        _storageOptions = storageOptions;
        _healthEventRepository = healthEventRepository;
        _cliDetectionService = cliDetectionService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DiagnosticBundlePreview> PreviewAsync(
        DiagnosticBundleRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveRequest = request ?? DiagnosticBundleRequest.Default;
        effectiveRequest.Validate();

        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);

        var previewId = Guid.NewGuid().ToString("N");
        var warnings = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        AddTextFile(files, BundleReadmeRelativePath, BuildReadme());

        AddTextFile(
            files,
            EnvironmentRelativePath,
            SanitizeJson(Serialize(BuildEnvironmentPayload(now))));

        AddTextFile(
            files,
            ConfigurationRelativePath,
            SanitizeJson(Serialize(BuildConfigurationPayload())));

        await AddCliStatusAsync(files, warnings, cancellationToken).ConfigureAwait(false);

        var schemaVersion = 0;

        if (effectiveRequest.IncludeDatabaseSchema)
        {
            var schema = await BuildDatabaseSchemaAsync(now, cancellationToken).ConfigureAwait(false);
            schemaVersion = schema.SchemaVersion;
            AddTextFile(files, DatabaseSchemaRelativePath, SanitizeJson(schema.Json));
        }

        if (effectiveRequest.IncludeHealthAudit)
        {
            var auditJson = await BuildHealthAuditJsonAsync(cancellationToken).ConfigureAwait(false);

            if (auditJson is not null)
            {
                AddTextFile(files, HealthAuditRelativePath, SanitizeJson(auditJson));
            }
        }

        var remainingLogBudget = effectiveRequest.MaxLogFiles;

        if (effectiveRequest.IncludeApplicationLogs)
        {
            remainingLogBudget -= CollectTextLogs(
                files,
                AppDataPaths.GetLogsDirectory(ResolveRootDirectory()),
                LogsPrefix,
                effectiveRequest,
                remainingLogBudget,
                warnings,
                cancellationToken);
        }

        if (effectiveRequest.IncludeRunLogs && remainingLogBudget > 0)
        {
            CollectRunLogs(files, effectiveRequest, remainingLogBudget, warnings, cancellationToken);
        }

        var scan = await _secretScanner.ScanFilesAsync(files, cancellationToken).ConfigureAwait(false);

        var manifest = BuildManifest(previewId, now, schemaVersion, files, CountSecretReferences(scan));
        var manifestJson = SanitizeJson(manifest.ToJson());
        AddTextFile(files, ManifestRelativePath, manifestJson);

        var finalScan = await _secretScanner.ScanFilesAsync(files, cancellationToken).ConfigureAwait(false);
        var blockingFindings = FilterBlockingFindings(finalScan);

        if (blockingFindings.Count > 0)
        {
            warnings.Add(
                $"{blockingFindings.Count} finding(s) survived redaction; the bundle cannot be exported.");
        }

        var referenceCount = CountSecretReferences(finalScan);

        var items = files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DiagnosticFileItem(
                pair.Key,
                ResolveCategory(pair.Key),
                pair.Value.LongLength,
                ComputeHash(pair.Value),
                IsRedacted: true))
            .ToArray();

        var preview = new DiagnosticBundlePreview
        {
            PreviewId = previewId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(PreviewLifetime),
            Request = effectiveRequest,
            Files = items,
            SecretScan = finalScan,
            BlockingFindings = blockingFindings,
            SecretReferenceCount = referenceCount,
            Warnings = warnings
        };

        lock (_cache)
        {
            _cache[previewId] = new CachedBundle(files, manifestJson, preview.ExpiresAtUtc);
        }

        return preview;
    }

    public async Task<DiagnosticBundleResult> CreateBundleAsync(
        DiagnosticBundlePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();

        CachedBundle? cached;

        lock (_cache)
        {
            if (!_cache.TryGetValue(preview.PreviewId, out cached)
                || cached.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException(
                    "The diagnostic bundle preview is unknown or expired; preview the bundle again before exporting.");
            }
            if (cached.ExportInProgress)
            {
                throw new InvalidOperationException("The diagnostic bundle preview is already being exported.");
            }
            cached.ExportInProgress = true;
        }

        try
        {
            if (preview.IsBlocked)
            {
                throw new DiagnosticBundleBlockedException(preview.BlockingFindings);
            }

            var scan = await _secretScanner.ScanFilesAsync(cached.Files, cancellationToken).ConfigureAwait(false);
            EnsureExportAllowed();
            var blockingFindings = FilterBlockingFindings(scan);

            if (blockingFindings.Count > 0)
            {
                throw new DiagnosticBundleBlockedException(blockingFindings);
            }

            var bundlePath = ResolveOutputPath(preview);
            WriteArchive(cached.Files, bundlePath, EnsureExportAllowed);

            // The atomic move committed the export. Consume the preview even if reading result
            // metadata fails; before that boundary, failures leave it available for retry.
            lock (_cache)
            {
                _cache.Remove(preview.PreviewId);
            }

            return new DiagnosticBundleResult(
                bundlePath,
                preview.PreviewId,
                cached.Files.Count,
                new FileInfo(bundlePath).Length,
                ComputeFileHash(bundlePath),
                _timeProvider.GetUtcNow(),
                cached.ManifestJson);
        }
        finally
        {
            lock (_cache)
            {
                cached.ExportInProgress = false;
            }
        }

        void EnsureExportAllowed()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cached.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException(
                    "The diagnostic bundle preview expired during export; preview the bundle again before exporting.");
            }
        }
    }

    private static IReadOnlyList<WorkflowSecretFinding> FilterBlockingFindings(WorkflowSecretScanReport report)
    {
        return report.Findings
            .Where(finding => !IsSecretReference(finding) && !IsAlreadyRedacted(finding))
            .ToArray();
    }

    private static bool IsSecretReference(WorkflowSecretFinding finding)
    {
        return string.Equals(finding.RuleName, SecretReferenceRule, StringComparison.Ordinal);
    }

    private static bool IsAlreadyRedacted(WorkflowSecretFinding finding)
    {
        return string.Equals(finding.RedactedSnippet, SensitiveDataFilter.Placeholder, StringComparison.Ordinal);
    }

    private static int CountSecretReferences(WorkflowSecretScanReport report)
    {
        return report.Findings.Count(IsSecretReference);
    }

    private object BuildEnvironmentPayload(DateTimeOffset now)
    {
        return new
        {
            capturedAtUtc = now,
            applicationVersion = ResolveApplicationVersion(),
            operatingSystem = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            machineName = Environment.MachineName,
            processorCount = Environment.ProcessorCount,
            culture = CultureInfo.CurrentCulture.Name,
            timeZone = TimeZoneInfo.Local.Id,
            appDataDirectory = MaskUserProfile(ResolveRootDirectory()),
            databasePath = MaskUserProfile(_connectionFactory.DatabasePath)
        };
    }

    private object BuildConfigurationPayload()
    {
        return new
        {
            appDataDirectory = MaskUserProfile(ResolveRootDirectory()),
            databaseFileName = _storageOptions.DatabaseFileName,
            logsDirectory = MaskUserProfile(AppDataPaths.GetLogsDirectory(ResolveRootDirectory())),
            diagnosticsDirectory = MaskUserProfile(
                AppDataPaths.GetDiagnosticBundlesDirectory(ResolveRootDirectory()))
        };
    }

    private async Task AddCliStatusAsync(
        Dictionary<string, byte[]> files,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_cliDetectionService is null)
        {
            return;
        }

        try
        {
            var snapshot = await _cliDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);

            AddTextFile(files, CliStatusRelativePath, SanitizeJson(Serialize(snapshot)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            warnings.Add($"CLI detection failed and was reported without paths: {exception.GetType().Name}.");
            AddTextFile(
                files,
                CliStatusRelativePath,
                Serialize(new { detection = "failed", reason = exception.GetType().Name }));
        }
    }

    private async Task<(string Json, int SchemaVersion)> BuildDatabaseSchemaAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var migrations = new List<object>();
        var schemaVersion = 0;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT version, name, applied_at_utc FROM _schema_migrations ORDER BY version;";

            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var version = reader.GetInt32(0);

                    schemaVersion = Math.Max(schemaVersion, version);
                    migrations.Add(new
                    {
                        version,
                        name = reader.GetString(1),
                        appliedAtUtc = reader.GetString(2)
                    });
                }
            }
            catch (SqliteException)
            {
                // An uninitialized database has no migrations table; the bundle reports version 0.
            }
        }

        var tables = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        return (Serialize(new
        {
            capturedAtUtc = now,
            schemaVersion,
            migrations,
            tables
        }), schemaVersion);
    }

    private async Task<string?> BuildHealthAuditJsonAsync(CancellationToken cancellationToken)
    {
        if (_healthEventRepository is null)
        {
            return null;
        }

        var events = await _healthEventRepository
            .ListRecentAsync(HealthAuditLimit, cancellationToken)
            .ConfigureAwait(false);

        var payload = events.Select(healthEvent => new
        {
            healthEvent.Id,
            healthEvent.ScopeType,
            healthEvent.ScopeId,
            previousState = healthEvent.PreviousState?.ToString(),
            newState = healthEvent.NewState.ToString(),
            errorClass = healthEvent.ErrorClass?.ToString(),
            reason = _sensitiveDataFilter.Redact(healthEvent.Reason),
            healthEvent.OccurredAt
        });

        return Serialize(payload);
    }

    private int CollectTextLogs(
        Dictionary<string, byte[]> files,
        string directory,
        string prefix,
        DiagnosticBundleRequest request,
        int budget,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (budget <= 0 || !Directory.Exists(directory))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var collected = 0;

        foreach (var path in Directory
                     .EnumerateFiles(directory, "*", options)
                     .Where(candidate => TextLogExtensions.Contains(Path.GetExtension(candidate)))
                     .OrderBy(candidate => candidate, StringComparer.Ordinal)
                     .Take(budget))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryAddLogFile(
                    files,
                    path,
                    $"{prefix}/{NormalizeRelativePath(Path.GetRelativePath(directory, path))}",
                    request.MaxLogBytesPerFile,
                    warnings))
            {
                collected++;
            }
        }

        return collected;
    }

    private void CollectRunLogs(
        Dictionary<string, byte[]> files,
        DiagnosticBundleRequest request,
        int budget,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var runsRoot = AppDataPaths.GetRunsRootDirectory(ResolveRootDirectory());

        if (budget <= 0 || !Directory.Exists(runsRoot))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var collected = 0;

        foreach (var runDirectory in Directory
                     .EnumerateDirectories(runsRoot, "*", options)
                     .OrderBy(directory => directory, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var fileName in RunLogFileNames)
            {
                if (collected >= budget)
                {
                    warnings.Add($"Run log budget of {budget} file(s) was reached.");
                    return;
                }

                var path = Path.Combine(runDirectory, fileName);

                if (!File.Exists(path))
                {
                    continue;
                }

                var relativePath =
                    $"{RunsPrefix}/{Path.GetFileName(runDirectory)}/{fileName}";

                if (TryAddLogFile(files, path, relativePath, request.MaxLogBytesPerFile, warnings))
                {
                    collected++;
                }
            }
        }
    }

    private bool TryAddLogFile(
        Dictionary<string, byte[]> files,
        string path,
        string relativePath,
        int maxBytes,
        List<string> warnings)
    {
        if (files.ContainsKey(relativePath))
        {
            return false;
        }

        try
        {
            var (text, truncated) = ReadTextCapped(path, maxBytes);

            if (truncated)
            {
                warnings.Add($"'{relativePath}' was truncated to the {maxBytes} byte bundle budget.");
            }

            if (text.Length == 0)
            {
                return false;
            }

            AddTextFile(files, relativePath, _sensitiveDataFilter.Redact(text));

            return true;
        }
        catch (IOException)
        {
            warnings.Add($"'{relativePath}' could not be read and was skipped.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add($"'{relativePath}' could not be read and was skipped.");
            return false;
        }
    }

    private static (string Text, bool Truncated) ReadTextCapped(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var length = stream.Length;
        var toRead = (int)Math.Min(length, maxBytes);
        var buffer = new byte[toRead];
        var read = 0;

        while (read < toRead)
        {
            var count = stream.Read(buffer, read, toRead - read);

            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var content = read == buffer.Length ? buffer : buffer[..read];

        return (DecodeText(content), length > maxBytes);
    }

    private static string DecodeText(byte[] bytes)
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

        return Encoding.UTF8.GetString(span);
    }

    private DiagnosticBundleManifest BuildManifest(
        string previewId,
        DateTimeOffset now,
        int schemaVersion,
        IReadOnlyDictionary<string, byte[]> files,
        int secretReferenceCount)
    {
        return new DiagnosticBundleManifest
        {
            PreviewId = previewId,
            BundleSchemaVersion = DiagnosticBundleManifest.CurrentSchemaVersion,
            GeneratedAtUtc = now,
            ApplicationVersion = ResolveApplicationVersion(),
            OperatingSystem = RuntimeInformation.OSDescription,
            MachineName = Environment.MachineName,
            Runtime = RuntimeInformation.FrameworkDescription,
            DatabaseSchemaVersion = schemaVersion,
            IncludedFiles = files.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            RedactedFileCount = files.Count,
            ReferenceCount = secretReferenceCount,
            ContainsSensitiveData = false
        };
    }

    private string ResolveOutputPath(DiagnosticBundlePreview preview)
    {
        var request = preview.Request;

        string path;

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            var directory = AppDataPaths.GetDiagnosticBundlesDirectory(ResolveRootDirectory());

            Directory.CreateDirectory(directory);

            path = Path.Combine(
                directory,
                $"diagnostic-bundle-{preview.CreatedAtUtc:yyyyMMdd-HHmmss}-{preview.PreviewId[..8]}.zip");
        }
        else
        {
            path = Path.GetFullPath(request.OutputPath);

            if (Directory.Exists(path))
            {
                path = Path.Combine(
                    path,
                    $"diagnostic-bundle-{preview.CreatedAtUtc:yyyyMMdd-HHmmss}-{preview.PreviewId[..8]}.zip");
            }
            else if (!Path.HasExtension(path))
            {
                path += ".zip";
            }
        }

        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The diagnostic bundle destination '{path}' already exists; choose another path.");
        }

        var parentDirectory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(parentDirectory))
        {
            throw new InvalidOperationException("The diagnostic bundle destination must be an absolute path.");
        }

        Directory.CreateDirectory(parentDirectory);

        return path;
    }

    private static void WriteArchive(
        IReadOnlyDictionary<string, byte[]> files,
        string destinationPath,
        Action ensureExportAllowed)
    {
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    ensureExportAllowed();
                    var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);

                    using var entryStream = entry.Open();

                    entryStream.Write(pair.Value, 0, pair.Value.Length);
                }
            }

            ensureExportAllowed();
            File.Move(tempPath, destinationPath);
        }
        catch
        {
            AtomicFile.TryDelete(tempPath);
            throw;
        }
    }

    private void AddTextFile(Dictionary<string, byte[]> files, string relativePath, string content)
    {
        files[relativePath] = Encoding.UTF8.GetBytes(content);
    }

    private string SanitizeJson(string json)
    {
        return MaskUserProfile(_sensitiveDataFilter.RedactJson(json));
    }

    private string MaskUserProfile(string value)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(profile) || value.Length == 0)
        {
            return value;
        }

        return value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRootDirectory()
    {
        return string.IsNullOrWhiteSpace(_storageOptions.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : _storageOptions.AppDataDirectory;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        lock (_cache)
        {
            foreach (var previewId in _cache
                         .Where(pair => pair.Value.ExpiresAtUtc <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _cache.Remove(previewId);
            }
        }
    }

    private static string ResolveCategory(string relativePath)
    {
        if (string.Equals(relativePath, ManifestRelativePath, StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.Manifest;
        }

        if (string.Equals(relativePath, EnvironmentRelativePath, StringComparison.Ordinal)
            || string.Equals(relativePath, CliStatusRelativePath, StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.Environment;
        }

        if (string.Equals(relativePath, HealthAuditRelativePath, StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.Audit;
        }

        if (relativePath.StartsWith("database/", StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.Database;
        }

        if (relativePath.StartsWith(LogsPrefix + "/", StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.Logs;
        }

        if (relativePath.StartsWith(RunsPrefix + "/", StringComparison.Ordinal))
        {
            return DiagnosticBundleCategories.RunLogs;
        }

        return DiagnosticBundleCategories.Configuration;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string ComputeHash(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, SerializerOptions);
    }

    private static string ResolveApplicationVersion()
    {
        var assembly = typeof(DiagnosticBundleService).Assembly;

        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string BuildReadme()
    {
        return string.Join(
            Environment.NewLine,
            "LLM Work GUI diagnostic bundle.",
            "Generated by the redacted diagnostic exporter.",
            string.Empty,
            "All text in this archive passes the redaction pipeline before it is added.",
            "The bundled manifest lists the files and confirms the scan result.",
            "Stored credentials are never copied; only reference identifiers can appear.",
            "Review the file list before sharing this archive.",
            string.Empty);
    }

    private sealed record CachedBundle(
        Dictionary<string, byte[]> Files,
        string ManifestJson,
        DateTimeOffset ExpiresAtUtc)
    {
        public bool ExportInProgress { get; set; }
    }
}

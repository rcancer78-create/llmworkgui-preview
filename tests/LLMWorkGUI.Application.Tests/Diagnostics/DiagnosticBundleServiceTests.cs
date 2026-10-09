using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Diagnostics;

public sealed class DiagnosticBundleServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Preview_ReportsFileMetadataAndStableCategories()
    {
        var service = await CreateServiceAsync();

        var preview = await service.PreviewAsync();

        Assert.False(preview.IsBlocked);
        Assert.NotEmpty(preview.Files);
        Assert.Contains(
            preview.Files,
            file => file.RelativePath == "environment.json"
                && file.Category == DiagnosticBundleCategories.Environment);
        Assert.Contains(
            preview.Files,
            file => file.RelativePath == "database/schema.json"
                && file.Category == DiagnosticBundleCategories.Database);
        Assert.Contains(
            preview.Files,
            file => file.RelativePath == "manifest.json"
                && file.Category == DiagnosticBundleCategories.Manifest);
        Assert.All(preview.Files, file => Assert.Equal(64, file.Sha256.Length));
        Assert.All(preview.Files, file => Assert.True(file.IsRedacted));
        Assert.True(preview.ExpiresAtUtc > preview.CreatedAtUtc);
    }

    [Fact]
    public async Task Preview_RedactsSecretsAndNeverWritesRawMaterialIntoTheArchive()
    {
        WriteLog(
            "app.log",
            "first line\n"
            + "apiKey: sk-1234567890abcdefgh\n"
            + "Authorization: Bearer abcdefgh12345678\n"
            + "password = \"super-secret-value\"\n");

        var outputPath = Path.Combine(_host.Root, "export", "bundle.zip");
        var service = await CreateServiceAsync();

        var preview = await service.PreviewAsync(new DiagnosticBundleRequest { OutputPath = outputPath });

        Assert.False(preview.IsBlocked);
        Assert.Contains(preview.Files, file => file.RelativePath == "logs/app.log");

        var result = await service.CreateBundleAsync(preview);

        Assert.Equal(outputPath, result.BundlePath);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(64, result.Sha256.Length);

        var log = ReadEntry(outputPath, "logs/app.log");

        Assert.DoesNotContain("sk-1234567890abcdefgh", log, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12345678", log, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);

        var manifest = JsonDocument.Parse(ReadEntry(outputPath, "manifest.json")).RootElement;

        Assert.False(manifest.GetProperty("containsSensitiveData").GetBoolean());
        Assert.Equal(0, manifest.GetProperty("referenceCount").GetInt32());
        Assert.True(manifest.GetProperty("redactedFileCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Preview_ReportsSecretReferencesWithoutBlockingTheExport()
    {
        WriteLog("refs.log", "route uses urn:llmworkgui:secret:openai-api-key\n");

        var service = await CreateServiceAsync();

        var preview = await service.PreviewAsync();

        Assert.False(preview.IsBlocked);
        Assert.True(preview.SecretReferenceCount >= 1);
        Assert.Empty(preview.BlockingFindings);

        var result = await service.CreateBundleAsync(preview);

        Assert.True(File.Exists(result.BundlePath));
    }

    [Fact]
    public async Task Preview_BlocksAndRefusesExportWhenASecretSurvivedRedaction()
    {
        var finding = new WorkflowSecretFinding("logs/app.log", 3, "OpenAiKey", "sk-live-material");
        var scanner = new StubSecretScanner(new WorkflowSecretScanReport(
            hasFindings: true,
            new[] { finding },
            new[] { "logs/app.log" }));

        WriteLog("app.log", "clean line\n");

        var service = await CreateServiceAsync(scanner);

        var preview = await service.PreviewAsync();

        Assert.True(preview.IsBlocked);
        Assert.Single(preview.BlockingFindings);

        var exception = await Assert.ThrowsAsync<DiagnosticBundleBlockedException>(
            () => service.CreateBundleAsync(preview));

        Assert.Single(exception.Findings);
        Assert.False(Directory.Exists(Path.Combine(_host.Root, "diagnostics")));
    }

    [Fact]
    public async Task Create_RequiresAPreviewFromTheSameServiceInstance()
    {
        var service = await CreateServiceAsync();
        var preview = await service.PreviewAsync();

        var otherService = await CreateServiceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => otherService.CreateBundleAsync(preview));
    }

    [Fact]
    public async Task Create_RefusesToOverwriteAnExistingBundle()
    {
        var outputPath = Path.Combine(_host.Root, "export", "bundle.zip");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, "occupied");

        var service = await CreateServiceAsync();
        var preview = await service.PreviewAsync(new DiagnosticBundleRequest { OutputPath = outputPath });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(preview));
    }

    [Fact]
    public async Task Preview_IncludesRedactedHealthAuditWhenARepositoryIsAvailable()
    {
        var healthEvents = new StubHealthEventRepository(new[]
        {
            new HealthEventRecord(
                "health-event-1",
                "account",
                "account-1",
                PreviousState: null,
                HealthState.Degraded,
                HealthErrorClass.NetworkOrTimeout,
                "account timeout apiKey=sk-deadbeef12345678",
                EvidenceRedactedJson: null,
                DateTimeOffset.UnixEpoch)
        });

        var service = await CreateServiceAsync(healthEvents: healthEvents);

        var preview = await service.PreviewAsync();

        Assert.False(preview.IsBlocked);
        Assert.Contains(preview.Files, file => file.RelativePath == "database/health-audit.json");

        var result = await service.CreateBundleAsync(preview);
        var audit = ReadEntry(result.BundlePath, "database/health-audit.json");

        Assert.DoesNotContain("sk-deadbeef12345678", audit, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_DoesNotLeakTheUserProfilePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(profile))
        {
            return;
        }

        // The input includes a profile path even when the SQLite fixture uses TEMP on another drive.
        // Only the configuration string points there; the archive and database remain in the fixture.
        var service = await CreateServiceAsync(appDataDirectory: Path.Combine(profile, "diagnostic-profile-fixture"));

        var preview = await service.PreviewAsync(new DiagnosticBundleRequest
        {
            OutputPath = Path.Combine(_host.Root, "profile-redaction.zip")
        });
        var result = await service.CreateBundleAsync(preview);
        var environment = ReadEntry(result.BundlePath, "environment.json");

        Assert.DoesNotContain(profile, environment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", environment, StringComparison.Ordinal);
    }

    private async Task<DiagnosticBundleService> CreateServiceAsync(
        IWorkflowSecretScanner? scanner = null,
        IHealthEventRepository? healthEvents = null,
        string? appDataDirectory = null)
    {
        await _host.InitializeAsync();

        var filter = new SensitiveDataFilter();

        return new DiagnosticBundleService(
            _host.Factory,
            scanner ?? new WorkflowSecretScanner(filter),
            filter,
            new StorageOptions
            {
                AppDataDirectory = appDataDirectory ?? _host.Root,
                DatabaseFileName = "llmworkgui.db"
            },
            healthEvents);
    }

    private void WriteLog(string name, string content)
    {
        var directory = Path.Combine(_host.Root, "logs");

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, name),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadEntry(string bundlePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(bundlePath);

        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"The bundle does not contain '{entryName}'.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private sealed class StubSecretScanner : IWorkflowSecretScanner
    {
        private readonly WorkflowSecretScanReport _report;

        public StubSecretScanner(WorkflowSecretScanReport report)
        {
            _report = report;
        }

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(
            ScratchWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_report);
        }

        public Task<WorkflowSecretScanReport> ScanFilesAsync(
            IReadOnlyDictionary<string, byte[]> files,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_report);
        }
    }

    private sealed class StubHealthEventRepository : IHealthEventRepository
    {
        private readonly IReadOnlyList<HealthEventRecord> _events;

        public StubHealthEventRepository(IReadOnlyList<HealthEventRecord> events)
        {
            _events = events;
        }

        public Task AppendAsync(HealthEventRecord healthEvent, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(
            string scopeType,
            string scopeId,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<HealthEventRecord>>(
                _events.Where(healthEvent => healthEvent.ScopeType == scopeType
                    && healthEvent.ScopeId == scopeId).ToArray());
        }

        public Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<HealthEventRecord>>(_events.Take(limit).ToArray());
        }
    }
}

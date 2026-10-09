using System.IO.Compression;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Diagnostics;

public sealed class DiagnosticBundleExportLifetimeTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly ManualClock _clock = new();
    private readonly GatedScanner _scanner = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_RevalidatesLifetimeAfterScan(bool cancel)
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "bundle.zip");
        using var cancellation = new CancellationTokenSource();
        _scanner.GateNextScan = true;
        var export = service.CreateBundleAsync(preview, cancellation.Token);
        try
        {
            await _scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancel)
                cancellation.Cancel();
            else
                _clock.UtcNow = preview.ExpiresAtUtc;
        }
        finally
        {
            _scanner.Release.TrySetResult();
        }

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => export);

        AssertNoArchiveOrTemporaryFile();
        if (cancel)
            Assert.True(File.Exists((await service.CreateBundleAsync(preview)).BundlePath));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(preview));
    }

    [Fact]
    public async Task Export_ExpiryBeforeAtomicMoveDeletesStagedArchive()
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "bundle.zip");
        var completedStagingObserved = false;
        _clock.ReadOverride = () =>
        {
            foreach (var path in Directory.GetFiles(_host.Root, "*.tmp"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    if (archive.Entries.Count > 0)
                    {
                        completedStagingObserved = true;
                        return preview.ExpiresAtUtc;
                    }
                }
                catch (IOException)
                {
                    // The writer still owns the staging file; advance time once it is closed.
                }
            }
            return _clock.UtcNow;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(preview));

        Assert.True(completedStagingObserved);
        AssertNoArchiveOrTemporaryFile();
    }

    [Fact]
    public async Task Export_AnotherPreviewCanFinishWhileFirstIsScanning()
    {
        var service = await CreateServiceAsync();
        var first = await PreviewAsync(service, "first.zip");
        var second = await PreviewAsync(service, "second.zip");
        _scanner.GateNextScan = true;
        var firstExport = service.CreateBundleAsync(first);
        try
        {
            await _scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await service.CreateBundleAsync(second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(result.BundlePath));
            Assert.False(firstExport.IsCompleted);
        }
        finally
        {
            _scanner.Release.TrySetResult();
            await firstExport;
        }
    }

    [Fact]
    public async Task Export_ClaimsPreviewUntilCommitAndRejectsReuseAtAnotherDestination()
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "first.zip");
        var second = preview with { Request = preview.Request with { OutputPath = Path.Combine(_host.Root, "second.zip") } };
        _scanner.GateNextScan = true;
        var firstExport = service.CreateBundleAsync(preview);
        try
        {
            await _scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(second));
            AssertNoArchiveOrTemporaryFile();
        }
        finally
        {
            _scanner.Release.TrySetResult();
            await firstExport;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(second));
        Assert.True(File.Exists(preview.Request.OutputPath));
        Assert.False(File.Exists(second.Request.OutputPath));
    }

    [Fact]
    public async Task Export_PreCanceledDoesNotScanOrConsumePreview()
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "bundle.zip");
        var scans = _scanner.ScanCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateBundleAsync(preview, cancellation.Token));
        Assert.Equal(scans, _scanner.ScanCount);
        AssertNoArchiveOrTemporaryFile();
        Assert.True(File.Exists((await service.CreateBundleAsync(preview)).BundlePath));
    }

    [Fact]
    public async Task Export_ScannerFailureReleasesPreviewForRetry()
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "bundle.zip");
        _scanner.FailNextScan = true;

        await Assert.ThrowsAsync<IOException>(() => service.CreateBundleAsync(preview));
        AssertNoArchiveOrTemporaryFile();
        Assert.True(File.Exists((await service.CreateBundleAsync(preview)).BundlePath));
    }

    [Fact]
    public async Task Export_DestinationFailureReleasesPreviewForAnotherPath()
    {
        var service = await CreateServiceAsync();
        var preview = await PreviewAsync(service, "occupied.zip");
        await File.WriteAllTextAsync(preview.Request.OutputPath!, "keep this file");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBundleAsync(preview));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(preview.Request.OutputPath!));
        var retry = preview with { Request = preview.Request with { OutputPath = Path.Combine(_host.Root, "retry.zip") } };
        Assert.True(File.Exists((await service.CreateBundleAsync(retry)).BundlePath));
    }

    private async Task<DiagnosticBundleService> CreateServiceAsync()
    {
        await _host.InitializeAsync();
        return new DiagnosticBundleService(_host.Factory, _scanner, new SensitiveDataFilter(),
            new StorageOptions { AppDataDirectory = _host.Root, DatabaseFileName = "llmworkgui.db" },
            timeProvider: _clock);
    }

    private Task<DiagnosticBundlePreview> PreviewAsync(DiagnosticBundleService service, string fileName) =>
        service.PreviewAsync(new DiagnosticBundleRequest { OutputPath = Path.Combine(_host.Root, fileName) });

    private void AssertNoArchiveOrTemporaryFile()
    {
        Assert.Empty(Directory.GetFiles(_host.Root, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_host.Root, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public Func<DateTimeOffset>? ReadOverride { get; set; }
        public override DateTimeOffset GetUtcNow() => ReadOverride?.Invoke() ?? UtcNow;
    }

    private sealed class GatedScanner : IWorkflowSecretScanner
    {
        private readonly WorkflowSecretScanner _inner = new(new SensitiveDataFilter());
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GateNextScan { get; set; }
        public bool FailNextScan { get; set; }
        public int ScanCount { get; private set; }

        public Task<WorkflowSecretScanReport> ScanScratchWorkspaceAsync(ScratchWorkspace workspace,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<WorkflowSecretScanReport> ScanFilesAsync(IReadOnlyDictionary<string, byte[]> files,
            CancellationToken cancellationToken = default)
        {
            ScanCount++;
            if (FailNextScan)
            {
                FailNextScan = false;
                throw new IOException("Synthetic scanner failure.");
            }
            if (GateNextScan)
            {
                GateNextScan = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            // Model a scanner that finishes successfully even after its caller cancels.
            return await _inner.ScanFilesAsync(files, CancellationToken.None);
        }
    }
}

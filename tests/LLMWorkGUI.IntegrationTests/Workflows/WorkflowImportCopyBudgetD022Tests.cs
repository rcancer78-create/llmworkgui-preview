using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowImportCopyBudgetD022Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrowingFilesAreRefusedWithinActualReadBudget(bool aggregate)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var directory = new TestDirectory();
        var root = directory.GetPath("source");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "a.bin"), new byte[8]);
        if (aggregate) await File.WriteAllBytesAsync(Path.Combine(root, "b.bin"), new byte[8]);
        var observed = new List<ReadCountingFile>();
        var hooks = new DirectoryImportTestHooks(MaxSingleFileBytes: 64,
            MaxUncompressedTotalBytes: aggregate ? 48 : 2048, MaxArchiveSizeBytes: 4096,
            BeforeFileCopy: path =>
            {
                var bytes = new byte[aggregate ? 40 : 512];
                new Random(Path.GetFileName(path) == "a.bin" ? 11 : 12).NextBytes(bytes);
                File.WriteAllBytes(path, bytes); // Deterministically after the metadata sample, before open.
            },
            OpenSource: path =>
            {
                var stream = new ReadCountingFile(path);
                observed.Add(stream);
                return stream;
            });
        var service = CreateService(database, hooks);

        await Assert.ThrowsAsync<WorkflowValidationException>(() => service.ImportDirectoryAsync(root, "actual growing bytes"));

        Assert.NotEmpty(observed);
        Assert.InRange(observed.Sum(stream => stream.BytesRead), 1, aggregate ? 49L : 65L);
        Assert.Equal(0, await database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(Path.Combine(database.Root, "workflows", "spool")));
    }

    [Fact]
    public async Task ZipHeadersCannotWritePastArchiveBudgetBeforeLateValidation()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var directory = new TestDirectory();
        var root = directory.GetPath("source");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 20; index++)
            await File.WriteAllBytesAsync(Path.Combine(root, $"{index:D2}-" + new string('n', 70) + ".bin"), new byte[] { 42 });
        WriteCountingFile? observed = null;
        var hooks = new DirectoryImportTestHooks(MaxSingleFileBytes: 64,
            MaxUncompressedTotalBytes: 1024, MaxArchiveSizeBytes: 1024,
            CreateSpool: path => observed = new WriteCountingFile(path));
        var service = CreateService(database, hooks);

        await Assert.ThrowsAsync<WorkflowValidationException>(() => service.ImportDirectoryAsync(root, "real ZIP overhead"));

        Assert.NotNull(observed);
        Assert.InRange(observed!.PeakLength, 1, 1024);
        Assert.Equal(0, await database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(Path.Combine(database.Root, "workflows", "spool")));
    }

    private static WorkflowImportService CreateService(TestDatabase database, DirectoryImportTestHooks hooks) => new(
        new SqliteWorkflowPackageRepository(database.Factory), new SqliteWorkflowVersionRepository(database.Factory),
        new WorkflowBlobStore(database.Root), new SafeArchiveValidator(), TimeProvider.System,
        NullLogger<WorkflowImportService>.Instance, new WorkflowManifestParser()) { DirectoryTestHooks = hooks };

    private sealed class ReadCountingFile(string path) : FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        public long BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await base.ReadAsync(buffer, token);
            BytesRead += read;
            return read;
        }
    }

    private sealed class WriteCountingFile(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 81920, FileOptions.Asynchronous)
    {
        public long PeakLength;
        private void Observe() => PeakLength = Math.Max(PeakLength, Length);
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, count); Observe(); }
        public override void Write(ReadOnlySpan<byte> buffer) { base.Write(buffer); Observe(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await base.WriteAsync(buffer, token); Observe(); }
    }
}

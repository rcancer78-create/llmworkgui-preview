using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowImportExportIntegrationTests
{
    [Fact]
    public async Task FailedFirstVersionInsertDoesNotPersistIncompletePackage()
    {
        await _database.InitializeAsync();
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectImportedVersion BEFORE INSERT ON WorkflowVersions BEGIN SELECT RAISE(ABORT, 'injected import failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => _importService.ImportZipAsync(
            new MemoryStream(CreateValidArchive()), "Atomic import"));
        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
    }

    [Fact]
    public async Task DuplicatePackageDoesNotBypassArchiveValidationWhenBlobIsMissing()
    {
        await _database.InitializeAsync();
        var invalid = System.Text.Encoding.UTF8.GetBytes("This is not a ZIP archive.");
        var hash = WorkflowTestArchiveFactory.ToBlobId(invalid);
        var now = DateTimeOffset.UtcNow;
        await _packageRepository.UpsertAsync(new WorkflowPackage("broken-historical-package", "Historical", null,
            Array.Empty<string>(), WorkflowSourceType.ZipArchive, hash, hash, now, now));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _importService.ImportZipAsync(
            new MemoryStream(invalid), "Rejected repair"));
        Assert.False(await _blobStore.BlobExistsAsync(hash));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
    }
    [Fact]
    public async Task ConcurrentImportsShareOneCommittedPackageAndFirstVersion()
    {
        await _database.InitializeAsync();
        var bytes = CreateValidArchive();
        using var barrier = new Barrier(2);
        Task<WorkflowImportResult> Import(string name) => Task.Run(async () =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return await _importService.ImportZipAsync(new MemoryStream(bytes), name);
        });
        var results = await Task.WhenAll(Import("First"), Import("Second")).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(results[0].Package.Id, results[1].Package.Id);
        Assert.Equal(results[0].Version.Id, results[1].Version.Id);
        Assert.Single(results.Where(result => result.IsDuplicate));
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.True(await _blobStore.VerifyBlobAsync(results[0].BlobId));
    }

}

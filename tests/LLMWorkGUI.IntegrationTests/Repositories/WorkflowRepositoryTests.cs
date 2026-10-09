using System.Globalization;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class WorkflowRepositoryTests : IDisposable
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);
    private static readonly string HashC = "sha256:" + new string('c', 64);

    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task WorkflowPackageRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();
        var repository = new SqliteWorkflowPackageRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var package = new WorkflowPackage(
            "pkg-1",
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release", "ci" },
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            timestamp,
            timestamp);

        await repository.UpsertAsync(package);

        var fetchedById = await repository.GetByIdAsync("pkg-1");
        Assert.NotNull(fetchedById);
        Assert.Equal("Release Workflow", fetchedById!.Name);
        Assert.Equal("Coordinates release tasks", fetchedById.Description);
        Assert.Equal(new[] { "release", "ci" }, fetchedById.Tags);
        Assert.Equal(WorkflowSourceType.ZipArchive, fetchedById.SourceType);
        Assert.Equal(HashA, fetchedById.OriginalHash);
        Assert.Equal(HashA, fetchedById.OriginalBlobId);
        Assert.Equal(timestamp, fetchedById.CreatedAtUtc);
        Assert.Equal(timestamp, fetchedById.UpdatedAtUtc);

        var fetchedByHash = await repository.GetByOriginalHashAsync(HashA);
        Assert.NotNull(fetchedByHash);
        Assert.Equal("pkg-1", fetchedByHash!.Id);

        var list = await repository.ListAsync();
        Assert.Single(list);

        var updated = new WorkflowPackage(
            "pkg-1",
            "Release Workflow v2",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.Directory,
            HashA,
            HashA,
            timestamp,
            timestamp.AddMinutes(30));

        await repository.UpsertAsync(updated);

        var afterUpdate = await repository.GetByIdAsync("pkg-1");
        Assert.NotNull(afterUpdate);
        Assert.Equal("Release Workflow v2", afterUpdate!.Name);
        Assert.Null(afterUpdate.Description);
        Assert.Empty(afterUpdate.Tags);
        Assert.Equal(WorkflowSourceType.Directory, afterUpdate.SourceType);
        Assert.Equal(timestamp.AddMinutes(30), afterUpdate.UpdatedAtUtc);
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));

        Assert.True(await repository.DeleteAsync("pkg-1"));
        Assert.Null(await repository.GetByIdAsync("pkg-1"));
        Assert.False(await repository.DeleteAsync("pkg-1"));
    }

    [Fact]
    public async Task WorkflowPackageRepository_StoresEmptyTagsAsJsonArrayAndReadsNullAsEmpty()
    {
        await _database.InitializeAsync();
        var repository = new SqliteWorkflowPackageRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var package = new WorkflowPackage(
            "pkg-tags",
            "Tagged Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashB,
            HashB,
            timestamp,
            timestamp);

        await repository.UpsertAsync(package);

        Assert.Equal("[]", await ReadScalarAsync("SELECT TagsJson FROM WorkflowPackages WHERE Id = 'pkg-tags';"));

        await ExecuteAsync("UPDATE WorkflowPackages SET TagsJson = NULL WHERE Id = 'pkg-tags';");

        var fetched = await repository.GetByIdAsync("pkg-tags");
        Assert.NotNull(fetched);
        Assert.Empty(fetched!.Tags);
    }

    [Fact]
    public async Task WorkflowPackageRepository_RejectsDuplicatedOriginalHash()
    {
        await _database.InitializeAsync();
        var repository = new SqliteWorkflowPackageRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new WorkflowPackage(
            "pkg-1",
            "First",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashC,
            HashC,
            timestamp,
            timestamp));

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertAsync(
            new WorkflowPackage(
                "pkg-2",
                "Second",
                null,
                Array.Empty<string>(),
                WorkflowSourceType.ZipArchive,
                HashC,
                HashC,
                timestamp,
                timestamp)));
    }

    [Fact]
    public async Task WorkflowVersionRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            "pkg-1",
            "Versioned Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            timestamp,
            timestamp));

        var firstVersion = new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            "{\"entrypoints\":[\"main\"]}",
            null,
            null,
            null,
            null,
            timestamp,
            null);

        var secondVersion = new WorkflowVersion(
            "ver-2",
            "pkg-1",
            2,
            HashB,
            HashB,
            WorkflowSourceType.Directory,
            null,
            "[\"executor\"]",
            "{}",
            null,
            null,
            timestamp.AddHours(1),
            timestamp.AddHours(2));

        await versionRepository.UpsertAsync(firstVersion);
        await versionRepository.UpsertAsync(secondVersion);

        var fetchedById = await versionRepository.GetByIdAsync("ver-2");
        Assert.NotNull(fetchedById);
        Assert.Equal("pkg-1", fetchedById!.WorkflowPackageId);
        Assert.Equal(2, fetchedById.VersionNumber);
        Assert.Equal(HashB, fetchedById.BlobId);
        Assert.Equal(HashB, fetchedById.OriginalHash);
        Assert.Equal(WorkflowSourceType.Directory, fetchedById.SourceType);
        Assert.Equal("[\"executor\"]", fetchedById.DeclaredRolesJson);
        Assert.Equal("{}", fetchedById.BindingsJson);
        Assert.Null(fetchedById.EntrypointsJson);
        Assert.Equal(timestamp.AddHours(2), fetchedById.ActivatedAtUtc);

        var fetchedByNumber = await versionRepository.GetByPackageAndVersionAsync("pkg-1", 1);
        Assert.NotNull(fetchedByNumber);
        Assert.Equal("ver-1", fetchedByNumber!.Id);
        Assert.Equal("{\"entrypoints\":[\"main\"]}", fetchedByNumber.EntrypointsJson);

        var list = await versionRepository.ListByPackageIdAsync("pkg-1");
        Assert.Equal(2, list.Count);
        Assert.Equal(new[] { "ver-1", "ver-2" }, list.Select(version => version.Id).ToArray());

        Assert.Null(await versionRepository.GetByPackageAndVersionAsync("pkg-1", 3));

        Assert.True(await versionRepository.DeleteAsync("ver-2"));
        Assert.False(await versionRepository.DeleteAsync("ver-2"));
        Assert.Single(await versionRepository.ListByPackageIdAsync("pkg-1"));
    }

    [Fact]
    public async Task WorkflowVersionRepository_CascadeDeletesVersionsWhenPackageIsDeleted()
    {
        await _database.InitializeAsync();
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            "pkg-cascade",
            "Cascade Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.Directory,
            HashA,
            HashA,
            timestamp,
            timestamp));

        await versionRepository.UpsertAsync(new WorkflowVersion(
            "ver-cascade",
            "pkg-cascade",
            1,
            HashA,
            HashA,
            WorkflowSourceType.Directory,
            null,
            null,
            null,
            null,
            null,
            timestamp,
            null));

        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));

        Assert.True(await packageRepository.DeleteAsync("pkg-cascade"));

        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Null(await versionRepository.GetByIdAsync("ver-cascade"));
    }

    [Fact]
    public async Task WorkflowVersionRepository_UpsertDoesNotMutateExistingVersion()
    {
        await _database.InitializeAsync();
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            "pkg-1",
            "Versioned Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            timestamp,
            timestamp));

        var original = new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            "{\"entrypoints\":[\"main\"]}",
            "[\"Executor\"]",
            null,
            null,
            null,
            timestamp,
            null);

        await versionRepository.UpsertAsync(original);
        await versionRepository.UpsertAsync(original); // Exact replay is idempotent.

        var mutated = new WorkflowVersion(
            "ver-1",
            "pkg-1",
            2,
            HashB,
            HashB,
            WorkflowSourceType.Directory,
            null,
            null,
            "{}",
            "{}",
            "{}",
            timestamp.AddDays(1),
            timestamp.AddDays(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => versionRepository.UpsertAsync(mutated));

        var fetched = await versionRepository.GetByIdAsync("ver-1");

        Assert.NotNull(fetched);
        Assert.Equal("pkg-1", fetched!.WorkflowPackageId);
        Assert.Equal(1, fetched.VersionNumber);
        Assert.Equal(HashA, fetched.BlobId);
        Assert.Equal(HashA, fetched.OriginalHash);
        Assert.Equal(WorkflowSourceType.ZipArchive, fetched.SourceType);
        Assert.Equal("{\"entrypoints\":[\"main\"]}", fetched.EntrypointsJson);
        Assert.Equal("[\"Executor\"]", fetched.DeclaredRolesJson);
        Assert.Null(fetched.BindingsJson);
        Assert.Null(fetched.ActivatedAtUtc);
        Assert.Equal(timestamp, fetched.CreatedAtUtc);

        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Null(await versionRepository.GetByPackageAndVersionAsync("pkg-1", 2));
    }

    [Fact]
    public async Task WorkflowVersionRepository_RejectsDeleteWhenVersionIsReferencedByBinding()
    {
        await _database.InitializeAsync();
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        var bindingRepository = new SqliteWorkflowBindingRepository(_database.Factory);
        var timestamp = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            "pkg-1",
            "Bound Workflow",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            timestamp,
            timestamp));

        await versionRepository.UpsertAsync(new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            timestamp,
            null));

        await ExecuteAsync(
            """
            INSERT INTO Projects
                (Id, DisplayName, RootPath, IsDirty, HasRequiredInstructions, DataClassification,
                 CreatedAtUtc, UpdatedAtUtc)
            VALUES ('project-1', 'Test project', 'C:\work\project-1', 0, 1, 'PrivateSource',
                    '2026-03-01T10:00:00.0000000+00:00', '2026-03-01T10:00:00.0000000+00:00');
            """);

        await bindingRepository.UpsertAsync(new WorkflowBinding(
            "8f14e45fceea167a5a36dedd4bea2543",
            "project-1",
            "pkg-1",
            "ver-1",
            routePolicyId: null,
            timestamp,
            timestamp));

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => versionRepository.DeleteAsync("ver-1"));

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await versionRepository.GetByIdAsync("ver-1"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));

        Assert.True(await bindingRepository.DeleteAsync("8f14e45fceea167a5a36dedd4bea2543"));
        Assert.True(await versionRepository.DeleteAsync("ver-1"));
    }

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }
}

using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class WorkflowBindingRepositoryTests : IDisposable
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);
    private static readonly string BindingId = "8f14e45fceea167a5a36dedd4bea2543";
    private static readonly string SecondBindingId = "45c48cce2e2d7fbdea1afc51c7c6ad26";

    private static readonly DateTimeOffset Timestamp = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Upsert_And_GetById_RoundTripsEveryField()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-1",
            routePolicyId: null,
            Timestamp,
            Timestamp));

        var fetched = await repository.GetByIdAsync(BindingId);

        Assert.NotNull(fetched);
        Assert.Equal(BindingId, fetched!.Id);
        Assert.Equal("project-1", fetched.ProjectId);
        Assert.Equal("pkg-1", fetched.WorkflowPackageId);
        Assert.Equal("ver-1", fetched.ActiveVersionId);
        Assert.Null(fetched.RoutePolicyId);
        Assert.Equal(Timestamp, fetched.CreatedAtUtc);
        Assert.Equal(Timestamp, fetched.UpdatedAtUtc);
    }

    [Fact]
    public async Task Upsert_OnConflict_UpdatesActiveVersionButPreservesIdAndCreatedAt()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);
        await SeedVersionAsync("pkg-1", "ver-2", HashB, versionNumber: 2);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);
        var createdAt = Timestamp;
        var updatedAt = Timestamp.AddHours(2);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-1",
            routePolicyId: null,
            createdAt,
            createdAt));

        // A repeated bind is identified by (ProjectId, WorkflowPackageId), so the row keeps its identity
        // and creation time while the active-version pointer moves.
        await repository.UpsertAsync(new WorkflowBinding(
            SecondBindingId,
            "project-1",
            "pkg-1",
            "ver-2",
            routePolicyId: null,
            createdAt,
            updatedAt));

        var fetched = await repository.GetByProjectAndPackageAsync("project-1", "pkg-1");

        Assert.NotNull(fetched);
        Assert.Equal(BindingId, fetched!.Id);
        Assert.Equal(createdAt, fetched.CreatedAtUtc);
        Assert.Equal("ver-2", fetched.ActiveVersionId);
        Assert.Equal(updatedAt, fetched.UpdatedAtUtc);
        Assert.Equal(1, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task Upsert_OnConflict_UpdatesRoutePolicyAndUpdatedAt()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);
        await SeedRoutingPolicyAsync("route-policy-1");

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-1",
            routePolicyId: null,
            Timestamp,
            Timestamp));

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId,
            "project-1",
            "pkg-1",
            "ver-1",
            "route-policy-1",
            Timestamp,
            Timestamp.AddMinutes(5)));

        var fetched = await repository.GetByProjectAndPackageAsync("project-1", "pkg-1");

        Assert.NotNull(fetched);
        Assert.Equal("route-policy-1", fetched!.RoutePolicyId);
        Assert.Equal(Timestamp.AddMinutes(5), fetched.UpdatedAtUtc);
    }

    [Fact]
    public async Task GetByProjectAndPackage_ReturnsNullWhenNoBindingExists()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        Assert.Null(await repository.GetByProjectAndPackageAsync("project-1", "pkg-missing"));
    }

    [Fact]
    public async Task ListByProjectId_ReturnsOnlyBindingsOfThatProject()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedProjectAsync("project-2");
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);
        await SeedPackageAndVersionAsync("pkg-2", "ver-2", HashB);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", null, Timestamp, Timestamp));
        await repository.UpsertAsync(new WorkflowBinding(
            SecondBindingId, "project-1", "pkg-2", "ver-2", null, Timestamp.AddMinutes(1), Timestamp));
        await repository.UpsertAsync(new WorkflowBinding(
            "d3d9446802a44259755d38e6d163e820", "project-2", "pkg-1", "ver-1", null, Timestamp, Timestamp));

        var bindings = await repository.ListByProjectIdAsync("project-1");

        Assert.Equal(2, bindings.Count);
        Assert.Equal(new[] { BindingId, SecondBindingId }, bindings.Select(binding => binding.Id).ToArray());
        Assert.Empty(await repository.ListByProjectIdAsync("project-missing"));
    }

    [Fact]
    public async Task Delete_RemovesTheBindingAndReportsMissingRows()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", null, Timestamp, Timestamp));

        Assert.True(await repository.DeleteAsync(BindingId));
        Assert.Null(await repository.GetByIdAsync(BindingId));
        Assert.False(await repository.DeleteAsync(BindingId));
    }

    [Fact]
    public async Task Upsert_AllowsTheSamePackageInDifferentProjects()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedProjectAsync("project-2");
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", null, Timestamp, Timestamp));
        await repository.UpsertAsync(new WorkflowBinding(
            SecondBindingId, "project-2", "pkg-1", "ver-1", null, Timestamp, Timestamp));

        Assert.Equal(2, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task Upsert_PersistsRoutePolicyReference()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);
        await SeedRoutingPolicyAsync("route-policy-1");

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", "route-policy-1", Timestamp, Timestamp));

        var fetched = await repository.GetByIdAsync(BindingId);

        Assert.NotNull(fetched);
        Assert.Equal("route-policy-1", fetched!.RoutePolicyId);
    }

    [Fact]
    public async Task Upsert_RejectsDuplicateBindingIdForAnotherProjectPair()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedProjectAsync("project-2");
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", null, Timestamp, Timestamp));

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-2", "pkg-1", "ver-1", null, Timestamp, Timestamp)));
    }

    [Fact]
    public async Task Upsert_CascadesWhenTheProjectIsDeleted()
    {
        await _database.InitializeAsync();
        await SeedProjectAsync();
        await SeedPackageAndVersionAsync("pkg-1", "ver-1", HashA);

        var repository = new SqliteWorkflowBindingRepository(_database.Factory);

        await repository.UpsertAsync(new WorkflowBinding(
            BindingId, "project-1", "pkg-1", "ver-1", null, Timestamp, Timestamp));

        await ExecuteAsync("DELETE FROM Projects WHERE Id = 'project-1';");

        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
    }

    private Task SeedProjectAsync(string projectId = "project-1") =>
        ExecuteAsync(
            """
            INSERT INTO Projects
                (Id, DisplayName, RootPath, IsDirty, HasRequiredInstructions, DataClassification,
                 CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $displayName, $rootPath, 0, 1, 'PrivateSource', $timestamp, $timestamp);
            """,
            ("$id", projectId),
            ("$displayName", $"Project {projectId}"),
            ("$rootPath", $@"C:\work\{projectId}"),
            ("$timestamp", TestDatabase.FormatTimestamp(Timestamp)));

    private async Task SeedPackageAndVersionAsync(string packageId, string versionId, string hash)
    {
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);

        await packageRepository.UpsertAsync(new WorkflowPackage(
            packageId,
            $"Package {packageId}",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            hash,
            hash,
            Timestamp,
            Timestamp));

        await SeedVersionAsync(packageId, versionId, hash);
    }

    private Task SeedVersionAsync(string packageId, string versionId, string hash, int versionNumber = 1)
    {
        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);

        return versionRepository.UpsertAsync(new WorkflowVersion(
            versionId,
            packageId,
            versionNumber,
            hash,
            hash,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Timestamp,
            null));
    }

    private async Task SeedRoutingPolicyAsync(string routePolicyId)
    {
        await ExecuteAsync(
            """
            INSERT INTO RoutingPolicies (Id, Name, ProjectId, Strategy, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Test policy', NULL, 'ManualOnly', 1, $timestamp, $timestamp);
            """,
            ("$id", routePolicyId),
            ("$timestamp", TestDatabase.FormatTimestamp(Timestamp)));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }
}

using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowBindingServiceTests : IDisposable
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);
    private static readonly string HashC = "sha256:" + new string('c', 64);

    private static readonly DateTimeOffset Timestamp = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private readonly MutableTimeProvider _time = new(Timestamp);

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task BindWorkflowToProject_CreatesBindingAndReturnsNewResult()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        var result = await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");

        Assert.True(result.IsNewBinding);
        Assert.Equal("project-1", result.Binding.ProjectId);
        Assert.Equal("pkg-1", result.Binding.WorkflowPackageId);
        Assert.Equal("ver-1", result.Binding.ActiveVersionId);
        Assert.Null(result.Binding.RoutePolicyId);
        Assert.Equal("pkg-1", result.Package.Id);
        Assert.Equal("ver-1", result.ActiveVersion.Id);
        Assert.Equal(Timestamp, result.Binding.CreatedAtUtc);
        Assert.Equal(Timestamp, result.Binding.UpdatedAtUtc);

        var stored = await new SqliteWorkflowBindingRepository(_database.Factory)
            .GetByProjectAndPackageAsync("project-1", "pkg-1");

        Assert.NotNull(stored);
        Assert.Equal(result.Binding.Id, stored!.Id);
    }

    [Fact]
    public async Task BindWorkflowToProject_OnRepeat_PreservesIdAndCreatedAtUtcAndReportsNotNew()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        var first = await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");

        _time.Advance(TimeSpan.FromMinutes(30));

        var second = await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-2");

        Assert.False(second.IsNewBinding);
        Assert.Equal(first.Binding.Id, second.Binding.Id);
        Assert.Equal(first.Binding.CreatedAtUtc, second.Binding.CreatedAtUtc);
        Assert.Equal(Timestamp.AddMinutes(30), second.Binding.UpdatedAtUtc);
        Assert.Equal("ver-2", second.Binding.ActiveVersionId);
        Assert.Equal(1, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task BindWorkflowToProject_PersistsRoutePolicy()
    {
        await InitializeWithPackagesAsync();
        await SeedRoutingPolicyAsync("route-policy-1");
        var service = CreateService();

        var result = await service.BindWorkflowToProjectAsync(
            "project-1",
            "pkg-1",
            "ver-1",
            "route-policy-1");

        Assert.Equal("route-policy-1", result.Binding.RoutePolicyId);

        var stored = await new SqliteWorkflowBindingRepository(_database.Factory)
            .GetByProjectAndPackageAsync("project-1", "pkg-1");

        Assert.NotNull(stored);
        Assert.Equal("route-policy-1", stored!.RoutePolicyId);
    }

    [Fact]
    public async Task BindWorkflowToProject_RejectsVersionFromAnotherPackage()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        // ver-3 belongs to pkg-2; binding it to pkg-1 would let the active pointer escape its package.
        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-3"));

        Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));
    }

    [Fact]
    public async Task BindWorkflowToProject_RejectsUnknownPackage()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.BindWorkflowToProjectAsync("project-1", "pkg-missing", "ver-1"));
    }

    [Fact]
    public async Task BindWorkflowToProject_RejectsUnknownVersion()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-missing"));
    }

    [Fact]
    public async Task SetActiveVersion_MovesPointerAndPreservesRoutePolicyAndCreationTime()
    {
        await InitializeWithPackagesAsync();
        await SeedRoutingPolicyAsync("route-policy-1");
        var service = CreateService();

        var bound = await service.BindWorkflowToProjectAsync(
            "project-1",
            "pkg-1",
            "ver-1",
            "route-policy-1");

        _time.Advance(TimeSpan.FromHours(1));

        var result = await service.SetActiveVersionAsync("project-1", "pkg-1", "ver-2");

        Assert.False(result.IsNewBinding);
        Assert.Equal(bound.Binding.Id, result.Binding.Id);
        Assert.Equal(bound.Binding.CreatedAtUtc, result.Binding.CreatedAtUtc);
        Assert.Equal("route-policy-1", result.Binding.RoutePolicyId);
        Assert.Equal("ver-2", result.Binding.ActiveVersionId);
        Assert.Equal(Timestamp.AddHours(1), result.Binding.UpdatedAtUtc);
    }

    [Fact]
    public async Task SetActiveVersion_DoesNotMutateTheVersionOrThePackage()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");

        var versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        var packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);

        var versionBefore = await versionRepository.GetByIdAsync("ver-2");
        var packageBefore = await packageRepository.GetByIdAsync("pkg-1");

        _time.Advance(TimeSpan.FromHours(2));

        await service.SetActiveVersionAsync("project-1", "pkg-1", "ver-2");

        var versionAfter = await versionRepository.GetByIdAsync("ver-2");
        var packageAfter = await packageRepository.GetByIdAsync("pkg-1");

        Assert.NotNull(versionBefore);
        Assert.NotNull(versionAfter);
        Assert.NotNull(packageBefore);
        Assert.NotNull(packageAfter);

        // Only the binding row moves: the version blob, its activation timestamp and the package are
        // byte-for-byte the same entities (ADR-0006 §1.7).
        Assert.Equal(versionBefore!.BlobId, versionAfter!.BlobId);
        Assert.Equal(versionBefore.OriginalHash, versionAfter.OriginalHash);
        Assert.Equal(versionBefore.ActivatedAtUtc, versionAfter.ActivatedAtUtc);
        Assert.Equal(versionBefore.CreatedAtUtc, versionAfter.CreatedAtUtc);
        Assert.Equal(packageBefore!.OriginalBlobId, packageAfter!.OriginalBlobId);
        Assert.Equal(packageBefore.UpdatedAtUtc, packageAfter.UpdatedAtUtc);
    }

    [Fact]
    public async Task SetActiveVersion_RejectsUnboundPackage()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.SetActiveVersionAsync("project-1", "pkg-1", "ver-1"));

        Assert.Contains("not bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActiveVersion_RejectsVersionFromAnotherPackage()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => service.SetActiveVersionAsync("project-1", "pkg-1", "ver-3"));

        var stored = await new SqliteWorkflowBindingRepository(_database.Factory)
            .GetByProjectAndPackageAsync("project-1", "pkg-1");

        Assert.NotNull(stored);
        Assert.Equal("ver-1", stored!.ActiveVersionId);
    }

    [Fact]
    public async Task UnbindWorkflow_RemovesOnlyTheBinding()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");

        Assert.True(await service.UnbindWorkflowAsync("project-1", "pkg-1"));

        Assert.Equal(0, await _database.CountAsync("WorkflowBindings"));

        // Unbinding never deletes the immutable package or version.
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages", "Id = 'pkg-1'"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions", "Id = 'ver-1'"));
    }

    [Fact]
    public async Task UnbindWorkflow_ReturnsFalseWhenNoBindingExists()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        Assert.False(await service.UnbindWorkflowAsync("project-1", "pkg-1"));
    }

    [Fact]
    public async Task GetBindingsForProject_ReturnsEveryBindingOfTheProject()
    {
        await InitializeWithPackagesAsync();
        var service = CreateService();

        await service.BindWorkflowToProjectAsync("project-1", "pkg-1", "ver-1");
        await service.BindWorkflowToProjectAsync("project-1", "pkg-2", "ver-3");

        var bindings = await service.GetBindingsForProjectAsync("project-1");

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, binding => binding.WorkflowPackageId == "pkg-1");
        Assert.Contains(bindings, binding => binding.WorkflowPackageId == "pkg-2");
    }

    [Fact]
    public void AddWorkflowServices_RegistersTheBindingContractsAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddWorkflowServices();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var repository = provider.GetRequiredService<IWorkflowBindingRepository>();
        var service = provider.GetRequiredService<IWorkflowBindingService>();

        Assert.IsType<SqliteWorkflowBindingRepository>(repository);
        Assert.IsType<WorkflowBindingService>(service);
        Assert.Same(repository, provider.GetRequiredService<IWorkflowBindingRepository>());
        Assert.Same(service, provider.GetRequiredService<IWorkflowBindingService>());
    }

    [Fact]
    public void AddInfrastructure_RegistersTheWorkflowBindingServices()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetService<IWorkflowBindingRepository>());
        Assert.NotNull(provider.GetService<IWorkflowBindingService>());
    }

    private WorkflowBindingService CreateService() =>
        new(
            new SqliteWorkflowBindingRepository(_database.Factory),
            new SqliteWorkflowPackageRepository(_database.Factory),
            new SqliteWorkflowVersionRepository(_database.Factory),
            _time);

    private async Task InitializeWithPackagesAsync()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        await SeedPackageAsync("pkg-1", HashA);
        await SeedPackageAsync("pkg-2", HashB);

        await SeedVersionAsync("pkg-1", "ver-1", HashA);
        await SeedVersionAsync("pkg-1", "ver-2", HashC, versionNumber: 2);
        await SeedVersionAsync("pkg-2", "ver-3", HashB);
    }

    private async Task SeedPackageAsync(string packageId, string hash)
    {
        await new SqliteWorkflowPackageRepository(_database.Factory).UpsertAsync(new WorkflowPackage(
            packageId,
            $"Package {packageId}",
            null,
            Array.Empty<string>(),
            WorkflowSourceType.ZipArchive,
            hash,
            hash,
            Timestamp,
            Timestamp));
    }

    private async Task SeedVersionAsync(
        string packageId,
        string versionId,
        string hash,
        int versionNumber = 1)
    {
        await new SqliteWorkflowVersionRepository(_database.Factory).UpsertAsync(new WorkflowVersion(
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
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RoutingPolicies (Id, Name, ProjectId, Strategy, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Test policy', NULL, 'ManualOnly', 1, $timestamp, $timestamp);
            """;
        command.Parameters.AddWithValue("$id", routePolicyId);
        command.Parameters.AddWithValue("$timestamp", TestDatabase.FormatTimestamp(Timestamp));

        await command.ExecuteNonQueryAsync();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}

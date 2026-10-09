using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowImportExportIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_AtomicActiveVersionMovePreservesLatePolicyAndRefusesAChangedPointer(bool pointerChanged)
    {
        await _database.InitializeAsync(); await _database.SeedRouteChainAsync(projectId: "project-1");
        var imported = await _importService.ImportZipAsync(new MemoryStream(CreateValidArchive()), "Binding race");
        var initial = imported.Version;
        var target = new WorkflowVersion("review-version-2", imported.Package.Id, 2, initial.BlobId, initial.OriginalHash,
            WorkflowSourceType.ZipArchive, null, null, null, null, null, DateTimeOffset.UtcNow, null);
        var competing = new WorkflowVersion("review-version-3", imported.Package.Id, 3, initial.BlobId, initial.OriginalHash,
            WorkflowSourceType.ZipArchive, null, null, null, null, null, DateTimeOffset.UtcNow, null);
        await _versionRepository.UpsertAsync(target); await _versionRepository.UpsertAsync(competing);
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO RoutingPolicies(Id,Name,Strategy,CreatedAtUtc,UpdatedAtUtc)
                VALUES('review-old','Old','Balanced','2026-10-06T00:00:00Z','2026-10-06T00:00:00Z'),
                ('review-new','New','Balanced','2026-10-06T00:00:00Z','2026-10-06T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var bound = await _bindingService.BindWorkflowToProjectAsync("project-1", imported.Package.Id, initial.Id, "review-old");
        var paused = new PausedBindingMutationRepository(_bindingRepository);
        var service = new WorkflowBindingService(paused, _packageRepository, _versionRepository, TimeProvider.System);
        var moving = service.SetActiveVersionAsync("project-1", imported.Package.Id, target.Id);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await _bindingRepository.UpsertAsync(new WorkflowBinding(bound.Binding.Id, "project-1", imported.Package.Id,
                pointerChanged ? competing.Id : initial.Id, "review-new", bound.Binding.CreatedAtUtc, DateTimeOffset.UtcNow));
        }
        finally { paused.Release.TrySetResult(); }
        try { await moving; }
        catch (WorkflowValidationException) when (pointerChanged) { }
        var stored = await _bindingRepository.GetByProjectAndPackageAsync("project-1", imported.Package.Id);
        Assert.Equal("review-new", stored!.RoutePolicyId);
        Assert.Equal(pointerChanged ? competing.Id : target.Id, stored.ActiveVersionId);
        Assert.Equal(bound.Binding.Id, stored.Id);
    }

    private sealed class PausedBindingMutationRepository(IWorkflowBindingRepository inner) : IWorkflowBindingRepository
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task PauseAsync(CancellationToken token)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
        public async Task UpsertAsync(WorkflowBinding binding, CancellationToken token = default)
        { await PauseAsync(token); await inner.UpsertAsync(binding, token); }
        public async Task<WorkflowBinding?> TrySetActiveVersionAsync(WorkflowBinding expected, string target,
            DateTimeOffset at, CancellationToken token = default)
        { await PauseAsync(token); return await inner.TrySetActiveVersionAsync(expected, target, at, token); }
        public Task<WorkflowBinding?> GetByIdAsync(string id, CancellationToken token = default) => inner.GetByIdAsync(id, token);
        public Task<WorkflowBinding?> GetByProjectAndPackageAsync(string project, string package, CancellationToken token = default) =>
            inner.GetByProjectAndPackageAsync(project, package, token);
        public Task<IReadOnlyList<WorkflowBinding>> ListByProjectIdAsync(string project, CancellationToken token = default) =>
            inner.ListByProjectIdAsync(project, token);
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => inner.DeleteAsync(id, token);
    }
}

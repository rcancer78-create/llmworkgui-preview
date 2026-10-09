using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Workflows;

/// <summary>
/// Binds immutable workflow packages to projects and moves the active-version pointer. Only the
/// WorkflowBindings row is written: the package, the version and the stored blob are never modified, and
/// <see cref="WorkflowVersion.ActivatedAtUtc"/> is never rewritten by a binding change (ADR-0006 §1.7).
/// </summary>
public sealed class WorkflowBindingService : IWorkflowBindingService
{
    private readonly IWorkflowBindingRepository _bindingRepository;
    private readonly IWorkflowPackageRepository _packageRepository;
    private readonly IWorkflowVersionRepository _versionRepository;
    private readonly TimeProvider _timeProvider;

    public WorkflowBindingService(
        IWorkflowBindingRepository bindingRepository,
        IWorkflowPackageRepository packageRepository,
        IWorkflowVersionRepository versionRepository,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(bindingRepository);
        ArgumentNullException.ThrowIfNull(packageRepository);
        ArgumentNullException.ThrowIfNull(versionRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _bindingRepository = bindingRepository;
        _packageRepository = packageRepository;
        _versionRepository = versionRepository;
        _timeProvider = timeProvider;
    }

    public async Task<WorkflowBindingResult> BindWorkflowToProjectAsync(
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        string? routePolicyId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeVersionId);

        var (package, version) = await ResolvePackageAndVersionAsync(
                workflowPackageId,
                activeVersionId,
                cancellationToken)
            .ConfigureAwait(false);

        var existing = await _bindingRepository
            .GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var binding = existing is null
            ? new WorkflowBinding(
                Guid.NewGuid().ToString("N"),
                projectId,
                workflowPackageId,
                activeVersionId,
                routePolicyId,
                now,
                now)
            : new WorkflowBinding(
                existing.Id,
                existing.ProjectId,
                existing.WorkflowPackageId,
                activeVersionId,
                routePolicyId,
                existing.CreatedAtUtc,
                now);

        await _bindingRepository.UpsertAsync(binding, cancellationToken).ConfigureAwait(false);

        // Composite UPSERT preserves the first durable identity when concurrent initial callers
        // both observed no binding. Return that row, never the losing caller's prototype ID.
        var stored = await _bindingRepository
            .GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null || stored.ActiveVersionId != activeVersionId || stored.RoutePolicyId != routePolicyId)
            throw new WorkflowValidationException("The workflow binding changed while binding the package; refresh and repeat the command.");

        return new WorkflowBindingResult(stored, package, version,
            IsNewBinding: existing is null && stored.Id == binding.Id);
    }

    public async Task<WorkflowBindingResult> SetActiveVersionAsync(
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeVersionId);

        var (package, version) = await ResolvePackageAndVersionAsync(
                workflowPackageId,
                activeVersionId,
                cancellationToken)
            .ConfigureAwait(false);

        var existing = await _bindingRepository
            .GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow package '{workflowPackageId}' is not bound to project '{projectId}'.");

        var binding = await _bindingRepository.TrySetActiveVersionAsync(existing, activeVersionId,
            _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)
            ?? throw new WorkflowValidationException("The workflow binding changed while moving its active version; refresh and repeat the command.");

        return new WorkflowBindingResult(binding, package, version, IsNewBinding: false);
    }

    public async Task<bool> UnbindWorkflowAsync(
        string projectId,
        string workflowPackageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPackageId);

        var existing = await _bindingRepository
            .GetByProjectAndPackageAsync(projectId, workflowPackageId, cancellationToken)
            .ConfigureAwait(false);

        return existing is not null
            && await _bindingRepository.DeleteAsync(existing.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorkflowBinding>> GetBindingsForProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        return _bindingRepository.ListByProjectIdAsync(projectId, cancellationToken);
    }

    private async Task<(WorkflowPackage Package, WorkflowVersion Version)> ResolvePackageAndVersionAsync(
        string workflowPackageId,
        string activeVersionId,
        CancellationToken cancellationToken)
    {
        var package = await _packageRepository
            .GetByIdAsync(workflowPackageId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow package '{workflowPackageId}' does not exist.");

        var version = await _versionRepository
            .GetByIdAsync(activeVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new WorkflowValidationException(
                $"The workflow version '{activeVersionId}' does not exist.");

        if (!string.Equals(version.WorkflowPackageId, package.Id, StringComparison.Ordinal))
        {
            throw new WorkflowValidationException(
                $"The workflow version '{activeVersionId}' does not belong to package '{workflowPackageId}'.");
        }

        return (package, version);
    }
}

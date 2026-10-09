using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Projects;

/// <summary>
/// Resolves the project a workspace send path is allowed to dispatch against. The active project is
/// bound before the data classification gate is evaluated, so a send never falls back to an
/// optimistic default classification: an unresolvable project resolves to <c>null</c> and the caller
/// stays fail-closed (ТЗ §6.5).
/// </summary>
public sealed class OpenedProjectResolver
{
    /// <summary>
    /// Application setting written by onboarding when the user confirms the working directory. It is
    /// the same key the onboarding wizard persists, so a confirmed workspace resolves to its project.
    /// </summary>
    public const string WorkspaceSettingKey = "onboarding.workspace";

    private readonly IProjectRepository _projectRepository;
    private readonly IApplicationSettingsRepository? _settingsRepository;

    public OpenedProjectResolver(
        IProjectRepository projectRepository,
        IApplicationSettingsRepository? settingsRepository = null)
    {
        ArgumentNullException.ThrowIfNull(projectRepository);

        _projectRepository = projectRepository;
        _settingsRepository = settingsRepository;
    }

    /// <summary>
    /// Resolves the opened project in strict priority order: an explicit project id, the confirmed
    /// onboarding workspace setting, and finally the single project in the repository. An explicit id
    /// that does not exist returns <c>null</c> instead of silently falling back to another project, a
    /// non-blank workspace setting that matches no project is unresolved without consulting the
    /// repository list, and an ambiguous repository (zero or several projects without a setting) is
    /// unresolved as well.
    /// </summary>
    public async Task<Project?> ResolveAsync(
        string? explicitProjectId,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitProjectId))
        {
            return await _projectRepository
                .GetByIdAsync(explicitProjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_settingsRepository is not null)
        {
            var workspacePath = await _settingsRepository
                .GetValueAsync(WorkspaceSettingKey, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                var project = await _projectRepository
                    .GetByRootPathAsync(workspacePath, cancellationToken)
                    .ConfigureAwait(false);

                if (project is not null)
                {
                    return project;
                }

                // Strict fail-closed: the confirmed workspace setting names a checkout that the
                // repository does not know. Falling through to ListAsync could bind an unrelated
                // single project and silently widen what the send path may classify (ТЗ §6.5).
                return null;
            }
        }

        var projects = await _projectRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        return projects.Count == 1 ? projects[0] : null;
    }
}

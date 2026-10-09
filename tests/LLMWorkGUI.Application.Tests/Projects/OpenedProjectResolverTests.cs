using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Projects;

public sealed class OpenedProjectResolverTests
{
    [Fact]
    public async Task ResolveAsync_ExplicitProjectIdThatDoesNotExist_ReturnsNull()
    {
        var projects = new InMemoryProjectRepository();
        projects.Save(CreateProject("proj-1", @"C:\work\one"));

        var resolver = new OpenedProjectResolver(projects, new InMemoryApplicationSettingsRepository());

        var resolved = await resolver.ResolveAsync("missing-project");

        // An explicit id never falls back to the workspace setting or the single project: an unknown
        // project must stay fail-closed instead of resolving to a different checkout (ТЗ §6.5).
        Assert.Null(resolved);
        Assert.Equal(1, projects.GetByIdCallCount);
        Assert.Equal(0, projects.GetByRootPathCallCount);
        Assert.Equal(0, projects.ListCallCount);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitProjectId_ReturnsItAndIgnoresSettingsAndList()
    {
        var projects = new InMemoryProjectRepository();
        var expected = CreateProject("proj-explicit", @"C:\work\explicit");
        projects.Save(expected);
        projects.Save(CreateProject("proj-setting", @"C:\work\setting"));

        var settings = new InMemoryApplicationSettingsRepository();
        settings.Set(OpenedProjectResolver.WorkspaceSettingKey, @"C:\work\setting");

        var resolver = new OpenedProjectResolver(projects, settings);

        var resolved = await resolver.ResolveAsync("proj-explicit");

        Assert.Same(expected, resolved);
        Assert.Equal(0, projects.GetByRootPathCallCount);
        Assert.Equal(0, projects.ListCallCount);
    }

    [Fact]
    public async Task ResolveAsync_BlankProjectIdWithWorkspaceSetting_ReturnsMatchingProjectByRootPath()
    {
        var projects = new InMemoryProjectRepository();
        projects.Save(CreateProject("proj-one", @"C:\work\one"));
        var expected = CreateProject("proj-two", @"C:\work\two");
        projects.Save(expected);

        var settings = new InMemoryApplicationSettingsRepository();
        settings.Set(OpenedProjectResolver.WorkspaceSettingKey, @"C:\work\two");

        var resolver = new OpenedProjectResolver(projects, settings);

        var resolved = await resolver.ResolveAsync(null);

        Assert.Same(expected, resolved);
        Assert.Equal(1, projects.GetByRootPathCallCount);
        Assert.Equal(0, projects.ListCallCount);
    }

    [Fact]
    public async Task ResolveAsync_NonMatchingSetting_ReturnsNullWithoutFallingThroughToSingleProject()
    {
        var projects = new InMemoryProjectRepository();
        projects.Save(CreateProject("proj-only", @"C:\work\only"));

        var settings = new InMemoryApplicationSettingsRepository();
        settings.Set(OpenedProjectResolver.WorkspaceSettingKey, @"C:\work\gone");

        var resolver = new OpenedProjectResolver(projects, settings);

        var resolved = await resolver.ResolveAsync(null);

        // The confirmed workspace no longer exists, so the lone remaining project must not be bound in
        // its place: the explicit user choice stays fail-closed instead of guessing a checkout (ТЗ §6.5).
        Assert.Null(resolved);
        Assert.Equal(1, projects.GetByRootPathCallCount);
        Assert.Equal(0, projects.ListCallCount);
    }

    [Fact]
    public async Task ResolveAsync_BlankProjectIdWithEmptySettingAndSingleProject_ReturnsThatProject()
    {
        var projects = new InMemoryProjectRepository();
        var expected = CreateProject("proj-only", @"C:\work\only");
        projects.Save(expected);

        var resolver = new OpenedProjectResolver(projects, new InMemoryApplicationSettingsRepository());

        var resolved = await resolver.ResolveAsync(null);

        Assert.Same(expected, resolved);
        Assert.Equal(1, projects.ListCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ResolveAsync_BlankProjectIdWithEmptySettingAndAmbiguousRepository_ReturnsNull(int projectCount)
    {
        var projects = new InMemoryProjectRepository();

        for (var index = 0; index < projectCount; index++)
        {
            projects.Save(CreateProject($"proj-{index}", $@"C:\work\{index}"));
        }

        var resolver = new OpenedProjectResolver(projects, new InMemoryApplicationSettingsRepository());

        var resolved = await resolver.ResolveAsync(null);

        Assert.Null(resolved);
    }

    private static Project CreateProject(string id, string rootPath) =>
        new(
            id,
            id,
            rootPath,
            null,
            false,
            false,
            null,
            null,
            DataClassification.PrivateSource);

    private sealed class InMemoryProjectRepository : IProjectRepository
    {
        private readonly Dictionary<string, Project> _projects = new(StringComparer.Ordinal);

        public int GetByIdCallCount { get; private set; }

        public int GetByRootPathCallCount { get; private set; }

        public int ListCallCount { get; private set; }

        public void Save(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            _projects[project.Id] = project;
        }

        public Task UpsertAsync(Project project, CancellationToken cancellationToken = default)
        {
            Save(project);
            return Task.CompletedTask;
        }

        public Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default)
        {
            GetByIdCallCount++;
            return Task.FromResult(_projects.GetValueOrDefault(projectId));
        }

        public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            GetByRootPathCallCount++;
            return Task.FromResult(_projects.Values.FirstOrDefault(
                project => string.Equals(project.RootPath, rootPath, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            return Task.FromResult<IReadOnlyList<Project>>(
                _projects.Values.OrderBy(project => project.Id, StringComparer.Ordinal).ToList());
        }

        public Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_projects.Remove(projectId));
    }

    private sealed class InMemoryApplicationSettingsRepository : IApplicationSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void Set(string key, string value) => _values[key] = value;

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(
            string key,
            string value,
            string valueType = "String",
            CancellationToken cancellationToken = default)
        {
            Set(key, value);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.Remove(key));

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(_values);
    }
}

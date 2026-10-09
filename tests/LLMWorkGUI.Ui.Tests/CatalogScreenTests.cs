using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CatalogScreenTests
{
    [Fact]
    public async Task ShippedProjectScreenReadsRepositoryAndFiltersWithoutLosingRows()
    {
        using var provider = UiTestHost.CreateProvider();
        var repository = provider.GetRequiredService<IProjectRepository>();
        await repository.UpsertAsync(Project("one", "Первый", @"C:\one"));
        await repository.UpsertAsync(Project("two", "Второй", @"C:\two"));
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var screen = Assert.IsType<CatalogScreenViewModel>(main.Screens.Single(s => s.Id == ScreenId.Projects));
        await screen.RefreshAsync();
        Assert.Equal(2, screen.Entries.Count);
        screen.SearchText = "ВТОРОЙ";
        Assert.Equal("two", Assert.Single(screen.Entries).Id);
        screen.SearchText = string.Empty;
        Assert.Equal(2, screen.Entries.Count);
    }

    [Fact]
    public async Task RegisteringFolderDefaultsToRestrictedAndDoesNotDuplicateIt()
    {
        var repository = new InMemoryProjectRepository();
        var screen = Create(ScreenId.Projects, repository);
        var directory = Directory.CreateTempSubdirectory("llm-catalog-");
        try
        {
            screen.ProjectPath = directory.FullName;
            await screen.AddProjectAsync();
            var project = Assert.Single(await repository.ListAsync());
            Assert.Equal(DataClassification.Restricted, project.DataClassification);
            Assert.Equal(directory.FullName, project.RootPath);
            screen.ProjectPath = directory.FullName + Path.DirectorySeparatorChar;
            await screen.AddProjectAsync();
            Assert.Single(await repository.ListAsync());
            Assert.Contains("уже добавлена", screen.StatusMessage);
        }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task InvalidFolderIsNotPersisted()
    {
        var repository = new InMemoryProjectRepository();
        var screen = Create(ScreenId.Projects, repository);
        screen.ProjectPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await screen.AddProjectAsync();
        Assert.Empty(await repository.ListAsync());
        Assert.Contains("не найдена", screen.StatusMessage);
        Assert.False(screen.IsBusy);
    }

    [Fact]
    public async Task SessionCatalogIncludesAllProjectsAndOrdersMostRecentFirst()
    {
        var projects = new InMemoryProjectRepository();
        await projects.UpsertAsync(Project("a", "Альфа", @"C:\a"));
        await projects.UpsertAsync(Project("b", "Бета", @"C:\b"));
        var sessions = new InMemorySessionStore();
        sessions.Add("old", SessionState.Closed, projectId: "a");
        sessions.Add("new", SessionState.Active, projectId: "b");
        var screen = new CatalogScreenViewModel(ScreenId.Sessions, "Сессии", "Ctrl+6", "Описание", "Пусто", "Подсказка", projects, sessions);
        await screen.RefreshAsync();
        Assert.Equal(new[] { "new", "old" }, screen.Entries.Select(row => row.Id));
        Assert.Equal("Активна", screen.Entries[0].Status);
        Assert.Equal("Бета", screen.Entries[0].Title);
    }

    [Fact]
    public async Task MissingRepositoryReportsUnavailableAndReleasesBusyState()
    {
        var screen = Create(ScreenId.Projects);
        await screen.RefreshAsync();
        Assert.Contains("недоступно", screen.StatusMessage);
        Assert.True(screen.IsEmpty);
        Assert.False(screen.IsBusy);
    }

    [Fact]
    public async Task ModelCatalogUsesBackendModelIdsInsteadOfLocalAccountKeys()
    {
        var models = new LibraryStubCatalogProvider();
        var screen = new CatalogScreenViewModel(ScreenId.Models, "Модели", "Ctrl+4", "Описание", "Пусто", "Подсказка", models: models);
        await screen.RefreshAsync();
        Assert.Equal("opencode/primary-model", Assert.Single(screen.Entries).Title);
        var model = models.Catalog.Models[0];
        models.Catalog = new SanitizedCapabilityCatalog(models.Catalog.Providers,
            new[] { model with { BackendModelId = null } }, models.Catalog.GeneratedAtUtc);
        await screen.RefreshAsync();
        Assert.Empty(screen.Entries);
    }

    [Fact]
    public async Task RunCatalogReadsExecutionsWithoutWritingAndNeverInventsObservedRoute()
    {
        var projects = new InMemoryProjectRepository();
        await projects.UpsertAsync(Project("a", "Альфа", @"C:\a"));
        var sessions = new InMemorySessionStore();
        sessions.Add("session", SessionState.Active, projectId: "a");
        var executions = new RunObservationExecutionStore();
        executions.Executions.Add("run", new Execution("run", "session", "request", ExecutionState.Queued,
            ExecutionFailureReason.None, "requested-route", null, null, null, null, null,
            Array.Empty<string>(), null, null, DateTimeOffset.UtcNow, null, null));
        var screen = new CatalogScreenViewModel(ScreenId.Runs, "Запуски", "Ctrl+7", "Описание", "Пусто", "Подсказка", projects, sessions, executions);
        await screen.RefreshAsync();
        var row = Assert.Single(screen.Entries);
        Assert.Equal("В очереди", row.Status);
        Assert.Equal("Маршрут ещё не подтверждён", row.Description);
        Assert.Equal(0, executions.Writes);
    }

    [Theory]
    [InlineData(ScreenId.Sessions, "Сессии пока не записаны.")]
    [InlineData(ScreenId.Runs, "Выполнения пока не записаны.")]
    public async Task UnavailableHistoryIsUnknownRatherThanConfirmedEmpty(ScreenId id, string empty)
    {
        var screen = new CatalogScreenViewModel(id, "Каталог", "Ctrl+6", "Описание", empty, "Подсказка");
        await screen.RefreshAsync();
        Assert.Contains("недоступно", screen.StatusMessage);
        Assert.NotEqual(empty, screen.EmptyStateMessage);
        Assert.Contains("не подтверждено", screen.EmptyStateMessage);
        Assert.Contains("неизвестно", screen.CountDisplay);
        Assert.True(screen.IsEmpty);
        Assert.False(screen.IsBusy);
    }

    [Theory]
    [InlineData(ScreenId.Sessions, "Сессии пока не записаны.")]
    [InlineData(ScreenId.Runs, "Выполнения пока не записаны.")]
    public async Task SuccessfulEmptyHistoryStillReportsConfirmedZero(ScreenId id, string empty)
    {
        var screen = new CatalogScreenViewModel(id, "Каталог", "Ctrl+6", "Описание", empty, "Подсказка",
            new InMemoryProjectRepository(), new InMemorySessionStore(), new RunObservationExecutionStore());
        await screen.RefreshAsync();
        Assert.Equal(string.Empty, screen.StatusMessage);
        Assert.Equal(empty, screen.EmptyStateMessage);
        Assert.Equal("Показано: 0 из 0", screen.CountDisplay);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedRefreshAndSuccessfulRecoveryPublishDistinctDataStates(bool unavailable)
    {
        var repository = new ControlledProjectRepository { Unavailable = unavailable };
        await repository.Inner.UpsertAsync(Project("owned", "Проект", @"C:\owned"));
        var screen = Create(ScreenId.Projects, repository);
        await screen.RefreshAsync();
        Assert.Equal("Показано: 1 из 1", screen.CountDisplay);
        repository.Fail = true;
        await screen.RefreshAsync();
        Assert.Empty(screen.Entries);
        Assert.Contains(unavailable ? "недоступно" : "Не удалось", screen.StatusMessage);
        Assert.Contains("не подтверждено", screen.EmptyStateMessage);
        Assert.Contains("неизвестно", screen.CountDisplay);
        repository.Fail = false;
        await screen.RefreshAsync();
        Assert.Single(screen.Entries);
        Assert.Equal("Показано: 1 из 1", screen.CountDisplay);
        Assert.Equal(string.Empty, screen.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulProjectRegistrationClearsPriorUnknownCount()
    {
        var repository = new ControlledProjectRepository { Fail = true };
        var screen = Create(ScreenId.Projects, repository);
        await screen.RefreshAsync();
        Assert.Contains("неизвестно", screen.CountDisplay);
        repository.Fail = false;
        var directory = Directory.CreateTempSubdirectory("llm-catalog-recovery-");
        try
        {
            screen.ProjectPath = directory.FullName;
            await screen.AddProjectAsync();
            Assert.Single(screen.Entries);
            Assert.Equal("Показано: 1 из 1", screen.CountDisplay);
            Assert.Contains("Проект добавлен", screen.StatusMessage);
        }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task SavedProjectWithFailedReloadReportsCommittedWriteAndUnknownCatalogThenRecovers()
    {
        var repository = new ControlledProjectRepository();
        await repository.Inner.UpsertAsync(Project("seed", "Прежний проект", @"C:\owned-seed"));
        var screen = Create(ScreenId.Projects, repository);
        await screen.RefreshAsync();
        var changed = new System.Collections.Generic.List<string?>();
        screen.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var directory = Directory.CreateTempSubdirectory("llm-catalog-committed-reload-");
        try
        {
            repository.FailAfterUpsert = true;
            screen.ProjectPath = directory.FullName;
            await screen.AddProjectAsync();
            Assert.Equal(2, (await repository.Inner.ListAsync()).Count);
            Assert.Equal(string.Empty, screen.ProjectPath);
            Assert.Empty(screen.Entries);
            Assert.Contains("Проект добавлен, но", screen.StatusMessage);
            Assert.Contains("не подтверждено", screen.EmptyStateMessage);
            Assert.Contains("неизвестно", screen.CountDisplay);
            Assert.Contains(nameof(screen.CountDisplay), changed);
            Assert.Contains(nameof(screen.EmptyStateMessage), changed);
            repository.Fail = false;
            await screen.RefreshAsync();
            Assert.Equal(2, screen.Entries.Count);
            Assert.Equal("Показано: 2 из 2", screen.CountDisplay);
            Assert.Equal(string.Empty, screen.StatusMessage);
        }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task FailedProjectWritePreservesInputAndPreviouslyConfirmedCatalog()
    {
        var repository = new ControlledProjectRepository();
        await repository.Inner.UpsertAsync(Project("seed", "Прежний проект", @"C:\owned-seed"));
        var screen = Create(ScreenId.Projects, repository);
        await screen.RefreshAsync();
        var directory = Directory.CreateTempSubdirectory("llm-catalog-write-failure-");
        try
        {
            repository.FailUpsert = true;
            screen.ProjectPath = directory.FullName;
            await screen.AddProjectAsync();
            Assert.Single(await repository.Inner.ListAsync());
            Assert.Single(screen.Entries);
            Assert.Equal(directory.FullName, screen.ProjectPath);
            Assert.Contains("Не удалось добавить проект", screen.StatusMessage);
            Assert.Equal("Показано: 1 из 1", screen.CountDisplay);
        }
        finally { directory.Delete(); }
    }

    private sealed class ControlledProjectRepository : IProjectRepository
    {
        public InMemoryProjectRepository Inner { get; } = new();
        public bool Fail { get; set; }
        public bool Unavailable { get; set; } = true;
        public bool FailAfterUpsert { get; set; }
        public bool FailUpsert { get; set; }
        public Task<System.Collections.Generic.IReadOnlyList<Project>> ListAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<System.Collections.Generic.IReadOnlyList<Project>>(Unavailable ? new InvalidOperationException("Owned unavailable fixture") : new IOException("Owned read failure fixture")) : Inner.ListAsync(cancellationToken);
        public async Task UpsertAsync(Project project, System.Threading.CancellationToken cancellationToken = default)
        {
            if (FailUpsert) throw new IOException("Owned write failure fixture");
            await Inner.UpsertAsync(project, cancellationToken);
            if (FailAfterUpsert) Fail = true;
        }
        public Task<Project?> GetByIdAsync(string id, System.Threading.CancellationToken cancellationToken = default) => Inner.GetByIdAsync(id, cancellationToken);
        public Task<Project?> GetByRootPathAsync(string path, System.Threading.CancellationToken cancellationToken = default) => Inner.GetByRootPathAsync(path, cancellationToken);
        public Task<bool> DeleteAsync(string id, System.Threading.CancellationToken cancellationToken = default) => Inner.DeleteAsync(id, cancellationToken);
    }

    private static CatalogScreenViewModel Create(ScreenId id, IProjectRepository? projects = null) =>
        new(id, "Каталог", "Ctrl+2", "Описание", "Пусто", "Подсказка", projects);

    private static Project Project(string id, string name, string path) =>
        new(id, name, path, null, false, false, null, null, DataClassification.PrivateSource);
}

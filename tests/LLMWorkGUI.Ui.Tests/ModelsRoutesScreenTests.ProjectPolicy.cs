using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class ModelsRoutesScreenTests
{
    [Fact]
    public void ProjectPolicyDoesNotWriteWithoutChangedClassAndExplicitConsent()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new ProjectPolicyFixture();
            var vm = new ProjectDataPolicyViewModel(service);
            Pump(vm.RefreshAsync()); vm.SelectedProject = vm.Projects.First();
            Assert.Equal(DataClassification.Restricted, vm.RequestedClass);
            vm.Confirmed = true;
            Assert.False(vm.CanSave);
            vm.RequestedClass = DataClassification.PublicSource;
            Assert.False(vm.Confirmed);
            Pump(vm.SaveAsync()); Assert.Equal(0, service.Writes);
            vm.Confirmed = true;
            Assert.True(vm.CanSave);
            Pump(vm.SaveAsync());
            Assert.Equal(1, service.Writes);
            Assert.Equal("revision-one", service.Expected!.Revision);
            Assert.Equal(DataClassification.PublicSource, service.Requested);
            Assert.False(vm.Confirmed);
            Assert.False(vm.CanSave);
        });
    }

    [Fact]
    public void ProjectPolicySelectionChangeRevokesPreviousConsent()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new ProjectPolicyFixture();
            var vm = new ProjectDataPolicyViewModel(service);
            Pump(vm.RefreshAsync()); vm.SelectedProject = vm.Projects.First();
            vm.RequestedClass = DataClassification.PrivateSource; vm.Confirmed = true;
            vm.SelectedProject = vm.Projects.Last();
            Assert.False(vm.Confirmed);
            Assert.Equal(DataClassification.PublicSource, vm.RequestedClass);
            Assert.Contains("second", vm.CurrentPolicy);
            Pump(vm.SaveAsync()); Assert.Equal(0, service.Writes);
        });
    }

    [Fact]
    public void ProjectPolicyCompareAndSwapRefusalRequiresNewObservationAndConsent()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new ProjectPolicyFixture { Refuse = true };
            var vm = new ProjectDataPolicyViewModel(service);
            Pump(vm.RefreshAsync()); vm.SelectedProject = vm.Projects.First();
            vm.RequestedClass = DataClassification.PublicSource; vm.Confirmed = true;
            Pump(vm.SaveAsync());
            Assert.Null(vm.SelectedProject);
            Assert.False(vm.Confirmed);
            Assert.False(vm.CanSave);
            Assert.Contains("изменён", vm.StatusMessage);
            Pump(vm.SaveAsync()); Assert.Equal(1, service.Writes);
        });
    }

    [Fact]
    public void ProjectPolicyDisappearedProjectClearsDisplayedObservationOnRefresh()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new ProjectPolicyFixture();
            var vm = new ProjectDataPolicyViewModel(service);
            Pump(vm.RefreshAsync()); vm.SelectedProject = vm.Projects.First();
            var notifications = new HashSet<string?>();
            vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            service.Empty = true;
            Pump(vm.RefreshAsync());
            Assert.Null(vm.SelectedProject);
            Assert.Equal("Выберите проект.", vm.CurrentPolicy);
            Assert.Contains(nameof(vm.SelectedProject), notifications);
            Assert.Contains(nameof(vm.CurrentPolicy), notifications);
            Assert.False(vm.CanSave);
        });
    }

    private sealed class ProjectPolicyFixture : IProjectDataPolicyService
    {
        public bool Empty { get; set; }
        public bool Refuse { get; init; }
        public int Writes { get; private set; }
        public ProjectDataPolicySnapshot? Expected { get; private set; }
        public DataClassification Requested { get; private set; }
        public Task<IReadOnlyList<ProjectDataPolicySnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectDataPolicySnapshot>>(Empty ? [] : [
                new("first", "First", @"D:\first", DataClassification.Restricted, "revision-one"),
                new("second", "Second", @"D:\second", DataClassification.PublicSource, "revision-two")]);
        public Task ChangeAsync(ProjectDataPolicySnapshot expected, DataClassification requested, bool confirmed, CancellationToken cancellationToken = default)
        {
            Assert.True(confirmed); Writes++; Expected = expected; Requested = requested;
            if (Refuse) throw new InvalidOperationException("Проект изменён другим действием.");
            return Task.CompletedTask;
        }
    }
}

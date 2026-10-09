using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using Microsoft.Extensions.DependencyInjection;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal static class UiTestHost
{
    public static ServiceProvider CreateProvider(
        ICliDetectionService? detectionService = null,
        IApplicationSettingsRepository? settingsRepository = null,
        IThemeResourceApplier? themeResourceApplier = null,
        ISystemThemeProvider? systemThemeProvider = null,
        TimeProvider? timeProvider = null,
        IHealthCenterService? healthCenter = null)
    {
        var services = new ServiceCollection();

        services.AddApplication();

        // The health service lives in Infrastructure, which this UI-only graph does not register. It is
        // supplied explicitly only when a test needs to prove the screen consumes it.
        if (healthCenter is not null)
        {
            services.AddSingleton(healthCenter);
        }
        services.AddSingleton<IApplicationSettingsRepository>(
            settingsRepository ?? new InMemoryApplicationSettingsRepository());
        services.AddSingleton<IAccountRepository>(new InMemoryAccountRepository());
        services.AddSingleton<IQuotaSnapshotRepository>(new InMemoryQuotaSnapshotRepository());
        services.AddSingleton<IThemeResourceApplier>(
            themeResourceApplier ?? new RecordingThemeResourceApplier());
        services.AddSingleton<ISystemThemeProvider>(
            systemThemeProvider ?? new FakeSystemThemeProvider());
        services.AddSingleton<ICliDetectionService>(
            detectionService ?? FakeCliDetectionService.Degraded());
        services.AddSingleton(timeProvider ?? new FakeTimeProvider());
        services.AddSingleton(new StorageOptions
        {
            AppDataDirectory = null,
            DatabaseFileName = StorageOptions.DefaultDatabaseFileName
        });
        // This UI-only graph must never restore or save the user's default shell-layout file.
        // Real persistence/composition tests build their own host with an isolated application-data root.
        services.AddSingleton<ILayoutPersistenceService>(new MemoryLayoutStore());

        // The workspace panels gate every send on the stored project classification, so the UI-only
        // graph provides deterministic repositories and the real gate instead of leaving them absent.
        // The built-in local profiles are seeded exactly like HostBootstrapper does, so the UI graph
        // exercises the shipped PrivateSource ceilings instead of an unseeded repository.
        services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        var profileRepository = new InMemoryProviderProfileRepository();
        BuiltInLocalProviderProfileSeeder.EnsureAsync(profileRepository).GetAwaiter().GetResult();
        services.AddSingleton<IProviderProfileRepository>(profileRepository);
        services.AddSingleton<IDataClassificationGate>(serviceProvider =>
            new DataClassificationGate(serviceProvider.GetService<IProviderProfileRepository>()));

        services.AddAppUi();

        return services.BuildServiceProvider();
    }

    private sealed class MemoryLayoutStore : ILayoutPersistenceService
    {
        private ShellLayoutState _state = new();
        public ShellLayoutState Load() => _state;
        public void Save(ShellLayoutState state) => _state = state;
    }
}

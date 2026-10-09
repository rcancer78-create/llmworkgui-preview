using System.Runtime.InteropServices;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.App.ViewModels;

public sealed class SettingsDiagnosticsViewModel : ScreenViewModel
{
    public SettingsDiagnosticsViewModel(
        ThemeSelectorViewModel themeSelector,
        CliStatusViewModel cliStatus,
        StorageOptions storageOptions)
        : base(
            ScreenId.SettingsDiagnostics,
            "Настройки / Диагностика",
            "Ctrl+0",
            "Пути, хранение, журналы и очищенный экспорт (ТЗ §7.2).")
    {
        ArgumentNullException.ThrowIfNull(themeSelector);
        ArgumentNullException.ThrowIfNull(cliStatus);
        ArgumentNullException.ThrowIfNull(storageOptions);

        ThemeSelector = themeSelector;
        CliStatus = cliStatus;

        var appDataDirectory = string.IsNullOrWhiteSpace(storageOptions.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : storageOptions.AppDataDirectory;

        AppDataDirectory = appDataDirectory;
        DatabasePath = AppDataPaths.GetDatabasePath(appDataDirectory, storageOptions.DatabaseFileName);
        LogsDirectory = AppDataPaths.GetLogsDirectory(appDataDirectory);
        DiagnosticBundlesDirectory = AppDataPaths.GetDiagnosticBundlesDirectory(appDataDirectory);
    }

    public ThemeSelectorViewModel ThemeSelector { get; }

    public CliStatusViewModel CliStatus { get; }

    public string AppDataDirectory { get; }

    public string DatabasePath { get; }

    public string LogsDirectory { get; }

    public string DiagnosticBundlesDirectory { get; }

    public string RuntimeDisplay => RuntimeInformation.FrameworkDescription;

    public string OperatingSystemDisplay => RuntimeInformation.OSDescription;

    public string RedactionNote =>
        "Журналы и диагностика проходят конвейер очистки; секреты никогда не записываются в логи, базу данных или файлы экспорта.";

    public string RedactedExportNote =>
        "Очищенный экспорт диагностики доступен на этапе стабилизации; указанные каталоги данных приложения являются текущим источником истины.";
}

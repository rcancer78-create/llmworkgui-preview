using System.IO;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ThemeServiceTests
{
    [Fact]
    public async Task InitializeAsync_WithoutStoredValue_DefaultsToSystemWithLightEffectiveTheme()
    {
        var repository = new InMemoryApplicationSettingsRepository();
        var systemThemeProvider = new FakeSystemThemeProvider { SystemTheme = AppTheme.Light };
        var applier = new RecordingThemeResourceApplier();
        var service = new ThemeService(repository, systemThemeProvider, applier);

        await service.InitializeAsync();

        Assert.Equal(AppTheme.System, service.CurrentTheme);
        Assert.Equal(AppTheme.Light, service.EffectiveTheme);
        Assert.Equal(new[] { AppTheme.Light }, applier.AppliedThemes);
        Assert.Null(service.LastPersistenceError);
    }

    [Fact]
    public async Task InitializeAsync_WithStoredDark_AppliesDarkThemeWithoutRaisingEvent()
    {
        var repository = new InMemoryApplicationSettingsRepository();
        await repository.SetValueAsync(ThemeService.ThemeSettingKey, "Dark");

        var applier = new RecordingThemeResourceApplier();
        var service = new ThemeService(repository, new FakeSystemThemeProvider(), applier);
        var eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        await service.InitializeAsync();

        Assert.Equal(AppTheme.Dark, service.CurrentTheme);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.Equal(new[] { AppTheme.Dark }, applier.AppliedThemes);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task InitializeAsync_WithInvalidStoredValue_FallsBackToSystem()
    {
        var repository = new InMemoryApplicationSettingsRepository();
        await repository.SetValueAsync(ThemeService.ThemeSettingKey, "Neon");

        var service = new ThemeService(
            repository,
            new FakeSystemThemeProvider { SystemTheme = AppTheme.Dark },
            new RecordingThemeResourceApplier());

        await service.InitializeAsync();

        Assert.Equal(AppTheme.System, service.CurrentTheme);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
    }

    [Fact]
    public async Task InitializeAsync_WhenRepositoryReadFails_DefaultsToSystemAndRecordsError()
    {
        var repository = new InMemoryApplicationSettingsRepository
        {
            GetException = new InvalidOperationException("private-bare-canary")
        };

        var service = new ThemeService(
            repository,
            new FakeSystemThemeProvider(),
            new RecordingThemeResourceApplier());

        await service.InitializeAsync();

        Assert.Equal(AppTheme.System, service.CurrentTheme);
        Assert.Equal(AppTheme.Light, service.EffectiveTheme);
        Assert.False(string.IsNullOrWhiteSpace(service.LastPersistenceError));
        Assert.DoesNotContain("private-bare-canary", service.LastPersistenceError);
    }

    [Fact]
    public async Task SetThemeAsync_AppliesPersistsAndRaisesThemeChanged()
    {
        var repository = new InMemoryApplicationSettingsRepository();
        var applier = new RecordingThemeResourceApplier();
        var service = new ThemeService(repository, new FakeSystemThemeProvider(), applier);
        await service.InitializeAsync();

        var events = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => events.Add(e);

        await service.SetThemeAsync(AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, service.CurrentTheme);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.Equal(ThemeService.ThemeSettingKey, repository.LastSetKey);
        Assert.Equal("Dark", repository.LastSetValue);
        Assert.Contains(AppTheme.Dark, applier.AppliedThemes);

        var themeEvent = Assert.Single(events);
        Assert.Equal(AppTheme.Dark, themeEvent.RequestedTheme);
        Assert.Equal(AppTheme.Dark, themeEvent.EffectiveTheme);
    }

    [Fact]
    public async Task SetThemeAsync_WithSystem_ResolvesEffectiveThemeFromSystemProvider()
    {
        var systemThemeProvider = new FakeSystemThemeProvider { SystemTheme = AppTheme.Dark };
        var applier = new RecordingThemeResourceApplier();
        var service = new ThemeService(
            new InMemoryApplicationSettingsRepository(),
            systemThemeProvider,
            applier);

        await service.InitializeAsync();
        applier.AppliedThemes.Clear();

        await service.SetThemeAsync(AppTheme.System);

        Assert.Equal(AppTheme.System, service.CurrentTheme);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.Equal(new[] { AppTheme.Dark }, applier.AppliedThemes);
    }

    [Fact]
    public async Task SetThemeAsync_WhenRepositoryWriteFails_StillAppliesThemeAndRecordsError()
    {
        var repository = new InMemoryApplicationSettingsRepository
        {
            SetException = new IOException("private-bare-canary")
        };

        var service = new ThemeService(
            repository,
            new FakeSystemThemeProvider(),
            new RecordingThemeResourceApplier());

        await service.SetThemeAsync(AppTheme.Dark);

        Assert.Equal(AppTheme.Dark, service.CurrentTheme);
        Assert.Equal(AppTheme.Dark, service.EffectiveTheme);
        Assert.False(string.IsNullOrWhiteSpace(service.LastPersistenceError));
        Assert.DoesNotContain("private-bare-canary", service.LastPersistenceError);
    }

    [Fact]
    public async Task ThemeSelector_DisplaysRussianThemeNamesAndPersistenceWarning()
    {
        var repository = new InMemoryApplicationSettingsRepository
        {
            SetException = new IOException("disk full")
        };
        var service = new ThemeService(
            repository,
            new FakeSystemThemeProvider { SystemTheme = AppTheme.Light },
            new RecordingThemeResourceApplier());
        await service.InitializeAsync();
        var selector = new ThemeSelectorViewModel(service);

        Assert.Equal("Текущая тема: Светлая", selector.EffectiveThemeDisplay);
        Assert.Null(selector.PersistenceWarning);

        await service.SetThemeAsync(AppTheme.Dark);

        Assert.Equal("Текущая тема: Тёмная", selector.EffectiveThemeDisplay);
        Assert.Equal(
            "Не удалось сохранить выбранную тему. Проверьте настройки приложения и повторите попытку.",
            selector.PersistenceWarning);
        Assert.DoesNotContain("disk full", selector.PersistenceWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetThemeAsync_WithSameTheme_DoesNotRaiseDuplicateEvent()
    {
        var service = new ThemeService(
            new InMemoryApplicationSettingsRepository(),
            new FakeSystemThemeProvider(),
            new RecordingThemeResourceApplier());

        await service.InitializeAsync();
        await service.SetThemeAsync(AppTheme.Light);

        var eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        await service.SetThemeAsync(AppTheme.Light);

        Assert.Equal(0, eventCount);
    }

    [Theory]
    [InlineData("Dark", AppTheme.Dark, true)]
    [InlineData("light", AppTheme.Light, true)]
    [InlineData("System", AppTheme.System, true)]
    [InlineData("", AppTheme.System, false)]
    [InlineData(null, AppTheme.System, false)]
    [InlineData("Neon", AppTheme.System, false)]
    public void TryParseTheme_ParsesSupportedValues(string? value, AppTheme expected, bool expectedParsed)
    {
        var parsed = ThemeService.TryParseTheme(value, out var theme);

        Assert.Equal(expected, theme);
        Assert.Equal(expectedParsed, parsed);
    }
}

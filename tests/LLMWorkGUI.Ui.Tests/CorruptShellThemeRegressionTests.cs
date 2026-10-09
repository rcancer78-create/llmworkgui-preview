using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CorruptShellThemeRegressionTests
{
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    public void UndefinedNumericThemeRetainsTheInitializedThemeAndRestoresOtherLayout(string storedTheme)
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var initialTheme = main.ThemeSelector.CurrentTheme;
        var shell = new UnifiedWorkspaceShellViewModel(main, new StoredLayout(storedTheme));

        var exception = Record.Exception(shell.ApplyPersistedLayout);

        Assert.Null(exception);
        Assert.Equal(initialTheme, main.ThemeSelector.CurrentTheme);
        Assert.Equal(270, shell.LeftPaneWidth);
    }

    [Fact]
    public void DefinedNumericThemeStillRestoresTheSelectedTheme()
    {
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var shell = new UnifiedWorkspaceShellViewModel(main, new StoredLayout(((int)AppTheme.Dark).ToString()));

        shell.ApplyPersistedLayout();

        Assert.Equal(AppTheme.Dark, main.ThemeSelector.CurrentTheme);
        Assert.Equal(270, shell.LeftPaneWidth);
    }

    private sealed class StoredLayout(string theme) : ILayoutPersistenceService
    {
        public ShellLayoutState Load() => new() { Theme = theme, LeftPaneWidth = 270 };
        public void Save(ShellLayoutState state) { }
    }
}

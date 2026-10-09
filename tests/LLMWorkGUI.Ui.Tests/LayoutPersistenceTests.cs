using System;
using System.IO;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class LayoutPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "LLMWorkGUI.Ui.Tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _filePath;

    public LayoutPersistenceTests()
    {
        _filePath = Path.Combine(_directory, "shell-layout.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        var service = new LayoutPersistenceService(_filePath);

        var state = service.Load();

        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, state.LeftPaneWidth);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, state.RightPaneWidth);
        Assert.True(state.IsLeftPaneVisible);
        Assert.True(state.IsRightPaneVisible);
        Assert.Equal(nameof(ScreenId.Workspace), state.ActiveScreen);
        Assert.Equal(string.Empty, state.Theme);
        Assert.Equal(_filePath, service.FilePath);
    }

    [Fact]
    public void Save_CreatesTheMissingDirectoryAndFile()
    {
        var service = new LayoutPersistenceService(_filePath);

        Assert.False(Directory.Exists(_directory));

        service.Save(new ShellLayoutState());

        Assert.True(File.Exists(_filePath));
        Assert.True(new FileInfo(_filePath).Length > 0);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsPanelGeometryVisibilityScreenAndTheme()
    {
        var state = new ShellLayoutState
        {
            LeftPaneWidth = 264,
            RightPaneWidth = 348,
            IsLeftPaneVisible = false,
            IsRightPaneVisible = true,
            ActiveScreen = nameof(ScreenId.HealthCenter),
            Theme = nameof(AppTheme.Dark)
        };

        var writer = new LayoutPersistenceService(_filePath);
        writer.Save(state);

        var reader = new LayoutPersistenceService(_filePath);
        var restored = reader.Load();

        Assert.Equal(264, restored.LeftPaneWidth);
        Assert.Equal(348, restored.RightPaneWidth);
        Assert.False(restored.IsLeftPaneVisible);
        Assert.True(restored.IsRightPaneVisible);
        Assert.Equal(nameof(ScreenId.HealthCenter), restored.ActiveScreen);
        Assert.Equal(nameof(AppTheme.Dark), restored.Theme);
    }

    [Fact]
    public void Load_WithCorruptJson_ReturnsDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_filePath, "this is not json {");

        var service = new LayoutPersistenceService(_filePath);

        var state = service.Load();

        Assert.Equal(ShellLayoutState.DefaultLeftPaneWidth, state.LeftPaneWidth);
        Assert.Equal(ShellLayoutState.DefaultRightPaneWidth, state.RightPaneWidth);
        Assert.Equal(nameof(ScreenId.Workspace), state.ActiveScreen);
    }

    /// <summary>
    /// An ordinary production start configures no application-data root, and the layout file must stay exactly
    /// where it has always been: the shell's memory is a comfort feature, and moving it out of the user
    /// profile would lose every saved layout.
    /// </summary>
    [Fact]
    public void AnUnconfiguredHostKeepsTheShippedUserProfileLayoutFile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LayoutPersistenceService.DefaultDirectoryName,
            LayoutPersistenceService.DefaultFileName);

        Assert.Equal(expected, new LayoutPersistenceService().FilePath);
        Assert.Equal(expected, LayoutPersistenceService.ResolveFilePath(appDataDirectory: null));
        Assert.Equal(expected, LayoutPersistenceService.ResolveFilePath(appDataDirectory: "   "));
    }

    /// <summary>
    /// A host composed over a configured root keeps its shell memory inside that root, which is what makes an
    /// isolated acceptance run unable to read or overwrite the real user's layout.
    /// </summary>
    [Fact]
    public void AConfiguredHostKeepsTheLayoutFileInsideItsOwnRoot()
    {
        var expected = Path.GetFullPath(Path.Combine(_directory, LayoutPersistenceService.DefaultFileName));

        Assert.Equal(expected, LayoutPersistenceService.ResolveFilePath(_directory));

        var service = new LayoutPersistenceService(LayoutPersistenceService.ResolveFilePath(_directory));

        service.Save(new ShellLayoutState { RightPaneWidth = 331 });

        Assert.Equal(expected, service.FilePath);
        Assert.True(File.Exists(expected));
        Assert.Equal(331, service.Load().RightPaneWidth);
    }
}

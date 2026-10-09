using System.Windows;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ThemeResourceTests
{
    [Fact]
    public void ThemeDictionaries_ExposeMatchingPaletteKeys()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            var light = ThemeResourceApplier.CreateThemeDictionary(AppTheme.Light);
            var dark = ThemeResourceApplier.CreateThemeDictionary(AppTheme.Dark);

            Assert.Equal("Light", light[ThemeResourceApplier.PaletteMarkerKey]);
            Assert.Equal("Dark", dark[ThemeResourceApplier.PaletteMarkerKey]);

            var lightKeys = CollectThemeKeys(light);
            var darkKeys = CollectThemeKeys(dark);

            Assert.Equal(lightKeys, darkKeys);
            Assert.Contains("Theme.Background", lightKeys);
            Assert.Contains("Theme.Surface", lightKeys);
            Assert.Contains("Theme.Border", lightKeys);
            Assert.Contains("Theme.Text.Primary", lightKeys);
            Assert.Contains("Theme.Text.Secondary", lightKeys);
            Assert.Contains("Theme.Accent", lightKeys);
            Assert.Contains("Theme.Warning.Surface", lightKeys);
            Assert.Contains("Theme.Badge.Synthetic.Background", lightKeys);
        });
    }

    [Fact]
    public void ApplyTheme_InsertsAndReplacesThemeDictionaryInApplicationResources()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            var application = System.Windows.Application.Current;
            Assert.NotNull(application);

            var applier = new ThemeResourceApplier();

            applier.ApplyTheme(AppTheme.Dark);
            Assert.Equal("Dark", FindAppliedPaletteName());

            var dictionaryCount = application.Resources.MergedDictionaries.Count;

            applier.ApplyTheme(AppTheme.Light);
            Assert.Equal("Light", FindAppliedPaletteName());
            Assert.Equal(dictionaryCount, application.Resources.MergedDictionaries.Count);
        });
    }

    [Fact]
    public void ApplyTheme_WithSystemTheme_Throws()
    {
        var applier = new ThemeResourceApplier();

        Assert.Throws<ArgumentException>(() => applier.ApplyTheme(AppTheme.System));
    }

    private static string? FindAppliedPaletteName()
    {
        var application = System.Windows.Application.Current;
        Assert.NotNull(application);

        foreach (var dictionary in application.Resources.MergedDictionaries)
        {
            if (dictionary.Contains(ThemeResourceApplier.PaletteMarkerKey))
            {
                return dictionary[ThemeResourceApplier.PaletteMarkerKey] as string;
            }
        }

        return null;
    }

    private static string[] CollectThemeKeys(ResourceDictionary dictionary)
    {
        return dictionary.Keys
            .Cast<object>()
            .Select(key => key.ToString() ?? string.Empty)
            .Where(key => key.StartsWith("Theme.", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }
}

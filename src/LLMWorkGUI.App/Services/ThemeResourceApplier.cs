using System.Collections.ObjectModel;
using System.Windows;

namespace LLMWorkGUI.App.Services;

public sealed class ThemeResourceApplier : IThemeResourceApplier
{
    public const string PaletteMarkerKey = "Theme.Palette.Name";

    private const string ThemeDictionaryPathFormat =
        "pack://application:,,,/LLMWorkGUI.App;component/Themes/{0}.xaml";

    public void ApplyTheme(AppTheme effectiveTheme)
    {
        if (effectiveTheme == AppTheme.System)
        {
            throw new ArgumentException("The effective theme must be Light or Dark.", nameof(effectiveTheme));
        }

        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("The WPF application is not initialized.");

        var dictionaries = application.Resources.MergedDictionaries;
        var dictionary = CreateThemeDictionary(effectiveTheme);
        var existingIndex = FindThemeDictionaryIndex(dictionaries);

        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = dictionary;
        }
        else
        {
            dictionaries.Insert(0, dictionary);
        }
    }

    public static ResourceDictionary CreateThemeDictionary(AppTheme effectiveTheme)
    {
        var source = new Uri(string.Format(ThemeDictionaryPathFormat, effectiveTheme), UriKind.Absolute);

        return new ResourceDictionary { Source = source };
    }

    private static int FindThemeDictionaryIndex(Collection<ResourceDictionary> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Contains(PaletteMarkerKey))
            {
                return index;
            }
        }

        return -1;
    }
}

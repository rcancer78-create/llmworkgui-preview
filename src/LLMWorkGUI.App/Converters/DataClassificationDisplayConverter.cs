using System.Globalization;
using System.Windows.Data;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.Converters;

/// <summary>
/// Prints an artifact classification the way the rest of the shipped screen names it.
/// <para>
/// Only the printed text is translated. The picker still carries the stored
/// <see cref="DataClassification"/> value in <c>SelectedItem</c>, so the classification the run service
/// records is the declared enum value and not a string this control invented.
/// </para>
/// </summary>
public sealed class DataClassificationDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DataClassification classification
            ? WorkflowLibraryViewModel.DescribeClassification(classification)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException(
            "The classification is chosen from the picker's own items and never parsed back out of its text.");
}

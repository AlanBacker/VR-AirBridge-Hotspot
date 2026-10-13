using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui;

/// <summary>Binding converter for <see cref="DisplayText.Shape"/>: <c>Text="{Binding X, Converter={StaticResource Disp}}"</c>.</summary>
public sealed class DisplayTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        DisplayText.Shape(value as string ?? value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Uses <see cref="StringTemplate"/> only for string content; element content (a label with a help icon, for example)
/// gets the default presentation instead of being shown as its type name.
/// </summary>
public sealed class StringTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string ? StringTemplate : null;
}

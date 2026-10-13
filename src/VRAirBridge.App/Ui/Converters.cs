using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VRAirBridge.App.Ui.Theme;

namespace VRAirBridge.App.Ui;

/// <summary>Semantic colour of a value / pill / dot.</summary>
public enum Tone { Normal, Dim, Accent, Good, Warn, Bad }

internal static class Tones
{
    public static string FgKey(Tone t) => t switch
    {
        Tone.Dim => "B.TextDim",
        Tone.Accent => "B.IconAccent",
        Tone.Good => "B.Good",
        Tone.Warn => "B.Warn",
        Tone.Bad => "B.Bad",
        _ => "B.Text",
    };

    public static string SoftKey(Tone t) => t switch
    {
        Tone.Accent => "B.AccentSoft",
        Tone.Good => "B.GoodSoft",
        Tone.Warn => "B.WarnSoft",
        Tone.Bad => "B.BadSoft",
        _ => "B.MutedSoft",
    };

    /// <summary>Dot colour: Normal / Dim use the muted grey.</summary>
    public static string DotKey(Tone t) => t switch
    {
        Tone.Accent => "B.Accent",
        Tone.Good => "B.Good",
        Tone.Warn => "B.Warn",
        Tone.Bad => "B.Bad",
        _ => "B.Muted",
    };
}

/// <summary>Tone → themed brush. Parameter: "fg" (default), "soft", "dot", "bg" (hint background), "border".</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = value is Tone tone ? tone : Tone.Normal;
        var key = (parameter as string) switch
        {
            "soft" => Tones.SoftKey(t),
            "dot" => Tones.DotKey(t),
            "bg" => t switch { Tone.Bad => "B.BadBg", Tone.Warn => "B.WarnBg", _ => "B.AccentBg" },
            "border" => t switch { Tone.Bad => "B.BadBorder", Tone.Warn => "B.WarnBorder", _ => "B.AccentBorder" },
            _ => Tones.FgKey(t),
        };
        return ThemeManager.Brush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public bool Hidden { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Hidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v ? (v == Visibility.Visible) != Invert : Binding.DoNothing;
}

/// <summary>null / empty string / zero count → Collapsed.</summary>
public sealed class EmptyToCollapsed : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };
        if (Invert) empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>double × factor (parameter, invariant culture).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double k = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return v * k;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Picks one of two geometries from a bool: parameter "I.a|I.b" (true → a).</summary>
public sealed class BoolToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "").Split('|');
        var key = value is bool b && b ? parts[0] : parts.Length > 1 ? parts[1] : parts[0];
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Resource key (string) → Geometry from Icons.xaml.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

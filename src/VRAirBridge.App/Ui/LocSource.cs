using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui;

/// <summary>
/// Binding source for localized strings: <c>{Binding [key], Source={x:Static LocSource.Instance}}</c>.
/// Raises the indexer change notification when the UI language changes, so every bound text updates live.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.LanguageChanged += () =>
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Raise();
            else d.BeginInvoke(Raise);
        };
    }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>XAML shorthand for a live localized string: <c>Text="{l:T ui.header.refresh}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var b = new Binding("[" + Key + "]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        // Text shown in a TextBlock / Run gets display shaping (Korean keep-all, non-breaking hyphens). Every other
        // target (automation names, tooltips, button content...) gets the plain string; their templates shape the display.
        if (IsDisplayText(serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget))
            b.Converter = Shaper;
        return b.ProvideValue(serviceProvider);
    }

    private static readonly DisplayTextConverter Shaper = new();

    private static bool IsDisplayText(IProvideValueTarget? t) => t?.TargetProperty switch
    {
        DependencyProperty dp => dp == TextBlock.TextProperty || dp == Run.TextProperty,
        _ => t?.TargetObject is Setter { Property: var p } && p == TextBlock.TextProperty,
    };
}

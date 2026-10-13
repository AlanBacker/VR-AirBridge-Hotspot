using System.Windows;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui;

/// <summary>
/// Code-only WPF application: merges the control styles, initialises the theme (dark / light / system)
/// and the font for the UI language. Shutdown is explicit (the app lives in the tray).
/// </summary>
internal sealed class AirBridgeApplication : Application
{
    public AirBridgeApplication(string? themeSetting)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var styles = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VRAirBridgeHotspot;component/Ui/Resources/Styles.xaml", UriKind.Absolute),
        };
        Resources.MergedDictionaries.Add(styles);
        ThemeManager.Initialize(Resources, themeSetting);
        ThemeManager.ApplyLanguage(Loc.Current);
        Loc.LanguageChanged += () =>
        {
            if (Dispatcher.CheckAccess()) ThemeManager.ApplyLanguage(Loc.Current);
            else Dispatcher.BeginInvoke(() => ThemeManager.ApplyLanguage(Loc.Current));
        };
    }
}

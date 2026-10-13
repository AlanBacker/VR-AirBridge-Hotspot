using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>
/// The in-app dialog card in its own small borderless window, for prompts that appear while no main window
/// exists (already running, uninstall, crash). Same look as the dialogs inside the main window.
/// </summary>
internal sealed class DialogWindow : Window
{
    private readonly DialogHostVm _host = new();
    private readonly DialogCard _card;

    private DialogWindow(DialogVm d)
    {
        Title = d.Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontSize = 12;
        SetResourceReference(ForegroundProperty, "B.Text");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/VRAirBridgeHotspot;component/app.ico", UriKind.Absolute)); }
        catch { }

        _card = new DialogCard { Margin = new Thickness(20), MaxWidth = 560 };
        var wa = SystemParameters.WorkArea;
        _card.BodyMaxHeight = Math.Max(200, wa.Height * 0.6);
        Content = _card;
        ThemeManager.Attach(this);

        _host.Copy = t => { try { Clipboard.SetText(t); } catch { } };
        _ = _host.ShowAsync(d);
        _card.DataContext = _host.Current;
        d.Result.ContinueWith(_ => Dispatcher.BeginInvoke(Close), TaskScheduler.Default);

        _card.DragHandle.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _host.Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is not ButtonBase) { _host.Accept(); e.Handled = true; }
        };
        Loaded += (_, _) => { Activate(); _card.FocusPrimary(); };
        Closing += (_, _) => { if (_host.IsOpen) _host.Cancel(); };
    }

    /// <summary>Shows the dialog modally and returns the id of the button that closed it.</summary>
    internal static string ShowModal(DialogVm d)
    {
        var w = new DialogWindow(d);
        w.ShowDialog();
        return d.Result.IsCompleted ? d.Result.Result : d.CancelResult;
    }
}

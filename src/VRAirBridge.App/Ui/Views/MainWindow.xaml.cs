using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>
/// Main window: header, start page / dashboard, sidebar, status bar, toasts and the in-app dialog layer.
/// Code-behind only handles view concerns (compact header, keyboard shortcuts for dialogs, focus requests).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Header shows icon-only buttons below this width (DIP).</summary>
    public const double CompactWidth = 1120;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    internal MainViewModel Vm { get; }

    internal MainWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        DataContext = vm;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/VRAirBridgeHotspot;component/app.ico", UriKind.Absolute));
        }
        catch { /* the window simply has no icon */ }
        ThemeManager.Attach(this);
        FitToWorkArea();
        SizeChanged += (_, _) => IsCompact = ActualWidth < CompactWidth;
        PreviewKeyDown += OnPreviewKeyDown;
        vm.FocusHotspotSettingsRequested += () => Dispatcher.BeginInvoke(() => Sidebar.FocusHotspotName(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Default 1200×780, but never larger than the work area (1080p at 125 % is 1536×~820 DIP).</summary>
    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        if (wa.Width <= 0 || wa.Height <= 0) return;
        if (Width > wa.Width - 16) Width = Math.Max(MinWidth, wa.Width - 16);
        if (Height > wa.Height - 16) Height = Math.Max(MinHeight, wa.Height - 16);
        if (MinWidth > wa.Width) MinWidth = Math.Max(640, wa.Width);
        if (MinHeight > wa.Height) MinHeight = Math.Max(480, wa.Height);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var dialogs = Vm.Dialogs;
        if (!dialogs.IsOpen) return;
        if (e.Key == Key.Escape)
        {
            dialogs.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not ButtonBase
                 && Keyboard.FocusedElement is not System.Windows.Controls.TextBox { AcceptsReturn: true, IsReadOnly: false })
        {
            dialogs.Accept();
            e.Handled = true;
        }
    }

    /// <summary>Lays the window out at its current size without showing it (screenshots).</summary>
    internal void LayoutOffscreen(double width, double height)
    {
        Width = width;
        Height = height;
        IsCompact = width < CompactWidth;
    }
}

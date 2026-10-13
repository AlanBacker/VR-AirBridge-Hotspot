using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>
/// Borderless popup window that shows <see cref="TrayMenu"/> next to the cursor (like a context menu) and hides
/// itself when it loses focus, on Esc, or after an item was clicked.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private bool _placing;

    internal TrayMenuWindow(MainViewModel vm)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontSize = 12;
        Left = -32000;
        Top = -32000;
        SetResourceReference(ForegroundProperty, "B.Text");
        DataContext = vm;
        Content = new TrayMenu();
        ThemeManager.Attach(this);

        Deactivated += (_, _) => { if (!_placing) Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        // Hide before the item's command runs (Click is raised before the command executes).
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Hide()));
    }

    /// <summary>Shows the menu at the mouse cursor, inside the work area of the cursor's monitor.</summary>
    internal void ShowAtCursor()
    {
        _placing = true;
        try
        {
            if (!IsVisible)
            {
                Left = -32000;
                Top = -32000;
                Show();
            }
            UpdateLayout();
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || !GetCursorPos(out var pt)) return;
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int w = (int)Math.Ceiling(ActualWidth * sx), hgt = (int)Math.Ceiling(ActualHeight * sy);

            var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            RECT wa = GetMonitorInfo(mon, ref mi) ? mi.rcWork : new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

            // The menu has a 12 DIP transparent margin for its shadow: let the card corner sit at the cursor.
            int inset = (int)(12 * sx);
            int x = pt.X - inset, y = pt.Y - hgt + inset;
            if (x + w > wa.Right) x = pt.X - w + inset;
            if (y < wa.Top) y = pt.Y - inset;
            x = Math.Max(wa.Left - inset, Math.Min(x, wa.Right - w + inset));
            y = Math.Max(wa.Top - inset, Math.Min(y, wa.Bottom - hgt + inset));
            SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */ | 0x0040 /* SWP_SHOWWINDOW */);
            SetForegroundWindow(h);
            Activate();
            Focus();
        }
        finally { _placing = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

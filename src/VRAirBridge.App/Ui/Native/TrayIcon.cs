using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui.Native;

/// <summary>
/// Notification-area icon through Shell_NotifyIcon (no WinForms). Owns a hidden tool window that receives the
/// icon's mouse messages and re-adds the icon when Explorer restarts. The icon is a coloured dot with a small
/// Wi-Fi mark, drawn with WPF and converted to an HICON.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint MSGFLT_ALLOW = 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly Dictionary<Color, IntPtr> _icons = new();
    private IntPtr _icon;
    private string _tip = "";
    private bool _added, _disposed;
    private DateTime _lastBalloon = DateTime.MinValue;

    /// <summary>Left click (or keyboard select) on the icon.</summary>
    public event Action? Activated;

    /// <summary>Right click / context-menu key: the menu should open at the cursor.</summary>
    public event Action? MenuRequested;

    /// <summary>The user clicked a balloon notification.</summary>
    public event Action? BalloonClicked;

    public TrayIcon(Color color, string tooltip)
    {
        var p = new HwndSourceParameters("VRAirBridgeHotspot.Tray")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // An elevated process does not receive Explorer's broadcast unless it is allowed through UIPI.
        try
        {
            ChangeWindowMessageFilterEx(_window.Handle, (uint)_taskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(_window.Handle, CallbackMessage, MSGFLT_ALLOW, IntPtr.Zero);
        }
        catch { /* best effort */ }

        _icon = IconFor(color);
        _tip = Clip(tooltip, 127);
        Add();
    }

    /// <summary>Window handle that owns the icon (foreground target for the menu).</summary>
    public IntPtr Handle => _window.Handle;

    public static int IconPixels => Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));

    public void SetColor(Color c)
    {
        if (_disposed) return;
        var h = IconFor(c);
        if (h == _icon) return;
        _icon = h;
        Modify(NIF_ICON);
    }

    public void SetTooltip(string text)
    {
        if (_disposed) return;
        text = Clip(text, 127);
        if (text == _tip) return;
        _tip = text;
        Modify(NIF_TIP);
    }

    /// <summary>Shows a balloon (Windows shows it as a toast); at most one per second.</summary>
    public void ShowBalloon(string title, string text, NotifyKind kind)
    {
        if (_disposed) return;
        if (DateTime.UtcNow - _lastBalloon < TimeSpan.FromSeconds(1)) return;
        _lastBalloon = DateTime.UtcNow;
        var d = NewData(NIF_INFO);
        d.szInfoTitle = Clip(title, 63);
        d.szInfo = Clip(string.IsNullOrWhiteSpace(text) ? title : text, 255);
        d.uTimeoutOrVersion = 5000;
        d.dwInfoFlags = kind switch { NotifyKind.Error => NIIF_ERROR, NotifyKind.Warning => NIIF_WARNING, _ => NIIF_INFO };
        try { Shell_NotifyIcon(NIM_MODIFY, ref d); } catch { }
    }

    private void Add()
    {
        var d = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        try { _added = Shell_NotifyIcon(NIM_ADD, ref d); } catch { _added = false; }
    }

    private void Modify(int flags)
    {
        var d = NewData(flags | NIF_TIP);
        try
        {
            if (!Shell_NotifyIcon(NIM_MODIFY, ref d) && !_added) Add();
        }
        catch { }
    }

    private NOTIFYICONDATAW NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Activated?.Invoke();
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    MenuRequested?.Invoke();
                    break;
                case NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private IntPtr IconFor(Color c)
    {
        if (_icons.TryGetValue(c, out var h)) return h;
        try { h = CreateDotIcon(c, IconPixels); }
        catch { h = IntPtr.Zero; }
        _icons[c] = h;
        return h;
    }

    /// <summary>Filled dot with a white ring and a small Wi-Fi mark, as an HICON (same drawing as before).</summary>
    internal static IntPtr CreateDotIcon(Color color, int size)
    {
        var bmp = RenderDot(color, size);
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        var px = new byte[stride * size];
        conv.CopyPixels(px, stride, 0);
        var mask = new byte[((size + 15) / 16) * 2 * size];
        var hColor = CreateBitmap(size, size, 1, 32, px);
        var hMask = CreateBitmap(size, size, 1, 1, mask);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = hColor, hbmMask = hMask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(hColor);
            DeleteObject(hMask);
        }
    }

    /// <summary>The tray dot as a WPF bitmap (also used by the screenshot renderer).</summary>
    internal static BitmapSource RenderDot(Color color, int size)
    {
        var dv = new DrawingVisual();
        using (var g = dv.RenderOpen())
        {
            double pad = Math.Max(1, size * 0.06);
            double d = size - 2 * pad - 0.5;
            var center = new Point(pad + d / 2, pad + d / 2);
            var ring = new Pen(Brushes.White, Math.Max(1, size / 12.0));
            ring.Freeze();
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            g.DrawEllipse(fill, ring, center, d / 2, d / 2);

            double cx = size / 2.0, cy = size * 0.66;
            var pen = new Pen(Brushes.White, Math.Max(1.2, size * 0.09)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            foreach (var k in new[] { 0.17, 0.31 })
            {
                double r = size * k;
                var a = new Point(cx + r * Math.Cos(225 * Math.PI / 180), cy + r * Math.Sin(225 * Math.PI / 180));
                var b = new Point(cx + r * Math.Cos(315 * Math.PI / 180), cy + r * Math.Sin(315 * Math.PI / 180));
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(a, false, false);
                    ctx.ArcTo(b, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                g.DrawGeometry(null, pen, geo);
            }
            double dot = Math.Max(1.5, size * 0.1);
            g.DrawEllipse(Brushes.White, null, new Point(cx, cy), dot / 2, dot / 2);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var d = NewData(0);
        try { Shell_NotifyIcon(NIM_DELETE, ref d); } catch { }
        foreach (var h in _icons.Values)
            if (h != IntPtr.Zero) DestroyIcon(h);
        _icons.Clear();
        try { _window.RemoveHook(WndProc); _window.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}

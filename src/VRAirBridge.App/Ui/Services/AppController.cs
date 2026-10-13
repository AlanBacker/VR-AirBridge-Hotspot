using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Native;
using VRAirBridge.App.Ui.ViewModels;
using VRAirBridge.App.Ui.Views;
using VRAirBridge.Core;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.App.Ui.Services;

/// <summary>
/// Owns the main window, the tray icon and the tray menu, and implements the window lifecycle of the old MainForm:
/// hidden start with --autostart, minimize / close to tray (one hint per session), the exit prompt while this
/// app hands out IPs, shutdown on Windows logoff, and the legacy --screenshot capture.
/// </summary>
internal sealed class AppController : IUiHost
{
    private readonly Watchdog _wd;
    private readonly AppSettings _s;
    private readonly AppOptions _opts;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly TrayIcon _tray;
    private readonly MainWindow _window;
    private TrayMenuWindow? _menu;
    private bool _exiting, _trayHintShown, _shownOnce;
    private OverallState? _trayState;

    internal MainViewModel Vm { get; }

    internal AppController(Watchdog wd, SettingsStore store, AppSettings s, AppOptions opts, bool isAdmin, string exePath)
    {
        _wd = wd;
        _s = s;
        _opts = opts;
        _tray = new TrayIcon(ColorFor(OverallState.Stopped), Loc.T("app.name"));
        _tray.Activated += ShowWindow;
        _tray.BalloonClicked += ShowWindow;
        _tray.MenuRequested += ShowMenu;

        Vm = new MainViewModel(wd, store, s, isAdmin, canElevate: !opts.NoElevate, exePath, this, mock: false,
                               languageOverride: CliLanguage(opts.Language));
        _window = new MainWindow(Vm);
        _window.StateChanged += OnStateChanged;
        _window.Closing += OnClosing;
        _window.ContentRendered += OnFirstRendered;
        Loc.LanguageChanged += OnLanguageChanged;
        if (Application.Current != null) Application.Current.SessionEnding += OnSessionEnding;

        if (!opts.AutoStart || opts.ScreenshotPath != null) ShowWindow();
    }

    // ================================================================== IUiHost

    public bool IsWindowShown => _window.IsVisible;

    public bool IsWindowVisible => _window.IsVisible && _window.WindowState != WindowState.Minimized;

    public void ShowWindow()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(ShowWindow); return; }
        if (_exiting && !_window.IsVisible) return;
        _menu?.Hide();
        bool first = !_shownOnce;
        _shownOnce = true;
        if (first) _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Bring to front even when another app owns the foreground (Topmost flip is the documented WPF way).
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
        Vm.OnWindowShown();
    }

    public void HideToTray()
    {
        _window.Hide();
        ShowTrayHintOnce();
    }

    public void UpdateTray(AppStatus st)
    {
        if (_trayState != st.State)
        {
            _trayState = st.State;
            _tray.SetColor(ColorFor(st.State));
        }
        _tray.SetTooltip(TooltipFor(st));
    }

    /// <summary>The tray menu binds to <see cref="MainViewModel.InternetAllowed"/>; nothing extra to do.</summary>
    public void SetTrayInternet(bool allow) { }

    public void Balloon(Notification n)
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => Balloon(n)); return; }
        _tray.ShowBalloon(n.Title, n.Text, n.Kind);
    }

    public void CopyText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return;
            }
            catch (COMException) { Thread.Sleep(40); }
            catch (Exception ex)
            {
                Logger.Warn(Loc.T("ui.opFailed", ex.Message));
                return;
            }
        }
    }

    public async Task ExitAsync(bool stopHotspot, bool ask)
    {
        if (_exiting) return;
        var st = _wd.Status;
        if (ask && !stopHotspot && st.Dhcp.Provider == DhcpProvider.Ours && st.Hotspot?.State == HotspotState.On)
        {
            ShowWindow();
            var d = DialogVm.Custom(Loc.T("dialog.exitTitle"), Loc.T("dialog.exitText"), DialogKind.Warning, "cancel",
                new DialogButtonVm(Loc.T("dialog.exitStop"), "stop", danger: true),
                new DialogButtonVm(Loc.T("dialog.exitKeep"), "keep"),
                new DialogButtonVm(Loc.T("dialog.dontExit"), "cancel", primary: true));
            var r = await Vm.Dialogs.ShowAsync(d);
            if (r != "keep" && r != "stop") return;
            if (_exiting) return;
            stopHotspot = r == "stop";
        }
        _exiting = true;
        Vm.SetExiting(stopHotspot);
        try { await _wd.ShutdownAsync(stopHotspot); }
        catch (Exception ex) { Logger.Warn(Loc.T("ui.opFailed", ex.Message)); }
        Finish();
    }

    // ================================================================== window lifecycle

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState == WindowState.Minimized && _s.MinimizeToTray && !_exiting)
            HideToTray();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_s.MinimizeToTray) HideToTray();
        else _ = ExitAsync(stopHotspot: !_s.KeepHotspotOnClose, ask: true);
    }

    /// <summary>Windows is logging off / shutting down: stop the watchdog (hotspot stays as it is) and go.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (_exiting) return;
        _exiting = true;
        Vm.SetExiting(false);
        try { Task.Run(() => _wd.ShutdownAsync(stopHotspot: false)).Wait(TimeSpan.FromSeconds(5)); } catch { }
        Finish();
    }

    private void Finish()
    {
        Vm.Detach();
        Loc.LanguageChanged -= OnLanguageChanged;
        try { _tray.Dispose(); } catch { }
        try { _menu?.Close(); } catch { }
        try { _window.Close(); } catch { }
        Application.Current?.Shutdown();
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.ShowBalloon(Loc.T("app.name"), Loc.T("ui.toast.trayHint"), NotifyKind.Info);
    }

    private void ShowMenu()
    {
        if (_exiting) return;
        _menu ??= new TrayMenuWindow(Vm);
        _menu.ShowAtCursor();
    }

    private void OnLanguageChanged()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(OnLanguageChanged); return; }
        if (!_exiting) _tray.SetTooltip(TooltipFor(_wd.Status));
    }

    /// <summary>The language chosen with --lang ("auto" or a known code), as Program applied it; null when not given or unknown.</summary>
    private static string? CliLanguage(string? arg) =>
        arg == null ? null : arg.Trim().Equals(Loc.Auto, StringComparison.OrdinalIgnoreCase) ? Loc.Auto : Loc.Normalize(arg);

    private static string TooltipFor(AppStatus st)
    {
        var text = Loc.T("tray.tooltip", Loc.T("app.name"), st.Headline);
        if (st.State == OverallState.Running)
        {
            text += Loc.T("tray.tooltipDevices", st.Clients.Count(c => c.Online));
            if (st.Hotspot?.Ssid is { Length: > 0 } ssid) text += "\n" + ssid;
        }
        return text;
    }

    internal static Color ColorFor(OverallState s) => s switch
    {
        OverallState.Running => Color.FromRgb(0x1E, 0x9E, 0x4A),
        OverallState.Retrying => Color.FromRgb(0xE0, 0x8A, 0x00),
        OverallState.Error => Color.FromRgb(0xD9, 0x30, 0x25),
        OverallState.Starting => Color.FromRgb(0x2F, 0x6F, 0xDE),
        _ => Color.FromRgb(0x6B, 0x72, 0x80),
    };

    // ================================================================== other instances

    /// <summary>Background threads that forward "show window" and "--internet on|off" from a second instance.</summary>
    internal void StartListeners(EventWaitHandle? show, EventWaitHandle? internetOn, EventWaitHandle? internetOff)
    {
        if (show != null)
        {
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        show.WaitOne();
                        if (_exiting) return;
                        _ui.BeginInvoke(ShowWindow);
                    }
                    catch { return; }
                }
            }) { IsBackground = true, Name = "ShowListener" }.Start();
        }
        if (internetOn != null && internetOff != null)
        {
            new Thread(() =>
            {
                var handles = new WaitHandle[] { internetOn, internetOff };
                while (true)
                {
                    try
                    {
                        int i = WaitHandle.WaitAny(handles);
                        if (_exiting) return;
                        bool allow = i == 0;
                        _ui.BeginInvoke(() => Vm.ApplyInternetFromCli(allow));
                    }
                    catch { return; }
                }
            }) { IsBackground = true, Name = "InternetListener" }.Start();
        }
    }

    // ================================================================== legacy --screenshot

    private async void OnFirstRendered(object? sender, EventArgs e)
    {
        _window.ContentRendered -= OnFirstRendered;
        if (_opts.ScreenshotPath == null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_opts.ScreenshotDelaySeconds));
            _window.Topmost = true;
            _window.Activate();
            await Task.Delay(600);
            var path = Path.GetFullPath(_opts.ScreenshotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 1) what is on screen; 2) what WPF renders (the screen copy shows the lock screen while locked).
            try { SavePng(CaptureScreen(_window), path); }
            catch (Exception ex) { Logger.Warn(Loc.T("ui.logline.screenshotFailed", ex.Message)); }
            SavePng(ScreenshotRenderer.RenderElement(_window.Content as FrameworkElement ?? _window, 1.0, null), Path.ChangeExtension(path, ".render.png"));
            Logger.Info(Loc.T("ui.logline.screenshotSaved", path));
        }
        catch (Exception ex) { Logger.Error(Loc.T("ui.logline.screenshotFailed", ex.Message)); }
        await ExitAsync(stopHotspot: false, ask: false);
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static BitmapSource CaptureScreen(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (!GetWindowRect(h, out var r)) throw new InvalidOperationException("GetWindowRect");
        int width = Math.Max(1, r.Right - r.Left), height = Math.Max(1, r.Bottom - r.Top);
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, width, height, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;
using VRAirBridge.App.Ui.Views;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui.Services;

/// <summary>
/// <c>--ui-screenshot</c>: renders the main window for every mock state × language × theme to PNG, off-screen, with
/// animations disabled. Files are named <c>&lt;state&gt;-&lt;lang&gt;-&lt;theme&gt;.png</c>; the tray menu is
/// <c>traymenu-&lt;lang&gt;-&lt;theme&gt;.png</c> and the minimum window size is <c>compact-…</c>.
/// Uses <see cref="MockData"/> only — the real config.json is never read.
/// </summary>
internal static class ScreenshotRenderer
{
    internal static readonly string[] ThemeNames = { ThemeManager.DarkSetting, ThemeManager.LightSetting };
    private const double DefaultWidth = 1200, DefaultHeight = 780, MinWidth = 1000, MinHeight = 640;

    /// <summary>Renders everything; returns (files written, failures).</summary>
    internal static (int Written, List<string> Failures) RenderAll(string dir, string exePath)
    {
        Directory.CreateDirectory(dir);
        bool motion = Motion.Enabled;
        Motion.Enabled = false;
        ThemeManager.RecreateOnApply = true;
        var failures = new List<string>();
        int written = 0;
        try
        {
            foreach (var lang in Loc.Codes)
            {
                Loc.SetLanguage(lang);
                ThemeManager.ApplyLanguage(lang);
                foreach (var theme in ThemeNames)
                {
                    ThemeManager.SetSetting(theme);
                    foreach (var name in MockData.SceneNames)
                    {
                        if (Try(failures, $"{name}-{lang}-{theme}", () => RenderScene(dir, name, name, lang, theme, exePath, DefaultWidth, DefaultHeight)))
                            written++;
                    }
                    if (Try(failures, $"compact-{lang}-{theme}", () => RenderScene(dir, "running", "compact", lang, theme, exePath, MinWidth, MinHeight)))
                        written++;
                    if (Try(failures, $"traymenu-{lang}-{theme}", () => RenderTrayMenu(dir, lang, theme, exePath)))
                        written++;
                }
            }
        }
        finally
        {
            Motion.Enabled = motion;
            ThemeManager.RecreateOnApply = false;
        }
        return (written, failures);
    }

    private static bool Try(List<string> failures, string what, Action a)
    {
        try
        {
            a();
            return true;
        }
        catch (Exception ex)
        {
            failures.Add(what + Loc.T("common.colon") + ex.Message);
            return false;
        }
    }

    private static MainViewModel NewVm(MockScene scene, string theme, string exePath)
    {
        scene.Settings.Ui.Theme = theme;
        var vm = new MainViewModel(null, null, scene.Settings, scene.IsAdmin, canElevate: false, exePath, new NullUiHost(), mock: true);
        vm.ApplyStatus(scene.Status);
        vm.SetMockLog(scene.Log);
        scene.Setup?.Invoke(vm);
        return vm;
    }

    private static void RenderScene(string dir, string scene, string fileState, string lang, string theme, string exePath, double width, double height)
    {
        var vm = NewVm(MockData.Build(scene), theme, exePath);
        var w = new MainWindow(vm)
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        w.MinWidth = Math.Min(w.MinWidth, width);
        w.MinHeight = Math.Min(w.MinHeight, height);
        w.LayoutOffscreen(width, height);
        try
        {
            w.Show();
            Settle(w);
            Save(RenderElement(w, 1.0, null), Path.Combine(dir, $"{fileState}-{lang}-{theme}.png"));
        }
        finally
        {
            w.Close();
            vm.Detach();
        }
    }

    private static void RenderTrayMenu(string dir, string lang, string theme, string exePath)
    {
        var vm = NewVm(MockData.Build("running"), theme, exePath);
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            FontSize = 12,
            DataContext = vm,
            Content = new TrayMenu(),
        };
        w.SetResourceReference(Window.ForegroundProperty, "B.Text");
        ThemeManager.Attach(w);
        try
        {
            w.Show();
            Settle(w);
            Save(RenderElement(w, 1.0, ThemeManager.Brush("B.Window")), Path.Combine(dir, $"traymenu-{lang}-{theme}.png"));
        }
        finally
        {
            w.Close();
            vm.Detach();
        }
    }

    /// <summary>
    /// Lets bindings, layout and Background-priority work (log scroll, focus requests) finish, and waits long enough
    /// for the short template storyboards (switch knobs, chevrons), which run on the real clock, to complete.
    /// </summary>
    private static void Settle(Window w)
    {
        for (int i = 0; i < 3; i++)
        {
            w.UpdateLayout();
            Pump(w.Dispatcher, DispatcherPriority.ContextIdle);
        }
        Wait(w.Dispatcher, 450);
        w.UpdateLayout();
        Pump(w.Dispatcher, DispatcherPriority.ContextIdle);
        w.UpdateLayout();
    }

    private static void Pump(Dispatcher d, DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        d.BeginInvoke(priority, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Wait(Dispatcher d, int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(DispatcherPriority.Background, d) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            frame.Continue = false;
        };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Renders an element (its own coordinate space) to a bitmap; optional backdrop for transparent windows.</summary>
    internal static BitmapSource RenderElement(FrameworkElement el, double scale, Brush? background)
    {
        double wDip = Math.Max(1, el.ActualWidth), hDip = Math.Max(1, el.ActualHeight);
        if (el is Window win && win.Content is FrameworkElement root && win.WindowStyle != WindowStyle.None)
        {
            // Normal window: ActualWidth includes the frame; render the client content instead.
            el = root;
            wDip = Math.Max(1, root.ActualWidth);
            hDip = Math.Max(1, root.ActualHeight);
            background ??= win.Background;
        }
        int pw = (int)Math.Ceiling(wDip * scale), ph = (int)Math.Ceiling(hDip * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background != null) dc.DrawRectangle(background, null, new Rect(0, 0, wDip, hDip));
            var vb = new VisualBrush(el) { Stretch = Stretch.Fill, Viewbox = new Rect(0, 0, wDip, hDip), ViewboxUnits = BrushMappingMode.Absolute };
            dc.DrawRectangle(vb, null, new Rect(0, 0, wDip, hDip));
        }
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

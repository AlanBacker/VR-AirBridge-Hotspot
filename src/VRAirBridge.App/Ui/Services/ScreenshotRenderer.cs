using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Controls;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;
using VRAirBridge.App.Ui.Views;
using VRAirBridge.Core;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.App.Ui.Services;

/// <summary>
/// <c>--ui-screenshot</c>: renders the main window for every mock state × language × theme to PNG, off-screen, with
/// animations disabled. Files are named <c>&lt;state&gt;-&lt;lang&gt;-&lt;theme&gt;.png</c>; the tray menu is
/// <c>traymenu-&lt;lang&gt;-&lt;theme&gt;.png</c> and the minimum window size is <c>compact-…</c>.
/// <c>layout\</c> holds the log-panel layout check (see <see cref="RenderLayoutChecks"/>).
/// Uses <see cref="MockData"/> only — the real config.json is never read.
/// </summary>
internal static class ScreenshotRenderer
{
    internal static readonly string[] ThemeNames = { ThemeManager.DarkSetting, ThemeManager.LightSetting };
    private const double DefaultWidth = 1200, DefaultHeight = 780, MinWidth = 1000, MinHeight = 640;

    // Lines for layout\layout-check.txt, collected during one RenderAll call.
    private static List<string> s_layoutReport = new();
    private static List<string> s_failures = new();

    /// <summary>Renders everything; returns (files written, failures).</summary>
    internal static (int Written, List<string> Failures) RenderAll(string dir, string exePath)
    {
        Directory.CreateDirectory(dir);
        bool motion = Motion.Enabled;
        Motion.Enabled = false;
        ThemeManager.RecreateOnApply = true;
        var failures = new List<string>();
        s_failures = failures;
        s_layoutReport = new List<string>
        {
            "Log panel layout check (--ui-screenshot).",
            "Part 1: every dashboard scene at the default window size; the page itself must not scroll (only the log list does).",
            "",
        };
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
            written += RenderLayoutChecks(Path.Combine(dir, "layout"), exePath, failures);
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

    private static MainWindow NewWindow(MainViewModel vm, double width, double height)
    {
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
        return w;
    }

    private static void RenderScene(string dir, string scene, string fileState, string lang, string theme, string exePath, double width, double height)
    {
        var vm = NewVm(MockData.Build(scene), theme, exePath);
        var w = NewWindow(vm, width, height);
        try
        {
            w.Show();
            Settle(w);
            Save(RenderElement(w, 1.0, null), Path.Combine(dir, $"{fileState}-{lang}-{theme}.png"));
            if (!vm.ShowStart && width >= DefaultWidth && height >= DefaultHeight)
                CheckPageFits(w, $"{fileState}-{lang}-{theme}");
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

    // ------------------------------------------------------------------ log panel layout check

    private sealed record LayoutCase(string Lang, string Theme, double Width, double Height, double Scale, bool Sidebar)
    {
        public string Name => string.Create(CultureInfo.InvariantCulture,
            $"log-{Lang}-{Theme}-{Width:0}x{Height:0}-{Scale * 100:0}pct-{(Sidebar ? "sidebar" : "full")}");
    }

    private static IEnumerable<LayoutCase> LayoutCases()
    {
        // Window sizes in DIPs. 100 %: maximized on a 1080p screen, the default size, minimum height at full width, the
        // minimum window (compact layout), a tall compact window. 125 %: the same, with the 1080p work area in DIPs.
        var at100 = new (double W, double H)[] { (1920, 1032), (1200, 780), (1200, 640), (1000, 640), (1000, 1032) };
        var at125 = new (double W, double H)[] { (1536, 826), (1200, 780), (1200, 640), (1000, 640), (1000, 826) };
        foreach (var (sizes, scale) in new[] { (at100, 1.0), (at125, 1.25) })
            foreach (var (wd, ht) in sizes)
                foreach (bool sidebar in new[] { true, false })
                    yield return new LayoutCase(Loc.ChineseSimplified, ThemeManager.DarkSetting, wd, ht, scale, sidebar);
        foreach (var (wd, ht) in at100)
            yield return new LayoutCase(Loc.English, ThemeManager.LightSetting, wd, ht, 1.0, true);
    }

    /// <summary>
    /// Renders the running dashboard with a long log (<see cref="MockData.LongLog"/>) at several window sizes, DPI scales
    /// and with the sidebar open/closed, and checks the log panel geometry: the page itself must not scroll unless the
    /// window is too short for the cards (log card at its minimum height), and the newest log line must be fully inside
    /// the log viewport, the page viewport and above the status bar, after load, after a live append and after a resize.
    /// Results go to <c>layout\layout-check.txt</c>; every violation is also reported as a failure.
    /// </summary>
    private static int RenderLayoutChecks(string dir, string exePath, List<string> failures)
    {
        Directory.CreateDirectory(dir);
        var report = s_layoutReport;
        report.AddRange(new[]
        {
            "",
            "Part 2: running dashboard with a long log (newest line last) at several sizes, DPI scales, sidebar open/closed.",
            "Columns: case | step | page scroll range/offset | log offset/max | newest line top..bottom | log viewport bottom | log card bottom | page viewport bottom | status bar top | result",
            "",
        });
        int written = 0;
        foreach (var c in LayoutCases())
            written += TryCount(failures, "layout " + c.Name, () => CheckLayout(dir, c, exePath, report, failures));
        File.WriteAllLines(Path.Combine(dir, "layout-check.txt"), report);
        return written;
    }

    private static int TryCount(List<string> failures, string what, Func<int> a)
    {
        int n = 0;
        Try(failures, what, () => n = a());
        return n;
    }

    private static int CheckLayout(string dir, LayoutCase c, string exePath, List<string> report, List<string> failures)
    {
        Loc.SetLanguage(c.Lang);
        ThemeManager.ApplyLanguage(c.Lang);
        ThemeManager.SetSetting(c.Theme);
        var vm = NewVm(MockData.LongLog(), c.Theme, exePath);
        vm.SidebarOpen = c.Sidebar;
        var w = NewWindow(vm, c.Width, c.Height);
        int written = 0;
        try
        {
            w.Show();
            if (c.Scale != 1.0) VisualTreeHelper.SetRootDpi(w, new DpiScale(c.Scale, c.Scale));
            Settle(w);
            var problems = new List<string>();
            double dpi = VisualTreeHelper.GetDpi(w).DpiScaleY;
            if (Math.Abs(dpi - c.Scale) > 0.001) problems.Add(Inv($"DPI scale is {dpi}, expected {c.Scale}"));
            if (Math.Abs(w.ActualWidth - c.Width) > 1 || Math.Abs(w.ActualHeight - c.Height) > 1)
                problems.Add(Inv($"window is {w.ActualWidth}x{w.ActualHeight}, expected {c.Width}x{c.Height}"));
            var items = w.Dashboard.LogList.Items;
            if (items.Count < 60 || items[items.Count - 1] is not LogLineVm last || !last.Text.EndsWith(MockData.NewestLongLogLine, StringComparison.Ordinal))
                problems.Add(Inv($"log has {items.Count} lines or the newest line is not the expected one"));

            Save(RenderElement(w, c.Scale, null), Path.Combine(dir, c.Name + ".png"));
            written++;
            if (CheckLog(w, c, "load", problems, report))
            {
                // the window is too short for all cards: the user scrolls the page down to the log
                Save(RenderElement(w, c.Scale, null), Path.Combine(dir, c.Name + "-scrolled.png"));
                written++;
            }

            vm.AppendMockLog(new LogEntry(DateTime.Today.AddHours(20).AddMinutes(22), LogLevel.Info,
                Loc.T("watchdog.deviceConnected", "Pixel-8", "192.168.137.102")));
            Settle(w);
            CheckLog(w, c, "live append", problems, report);

            double h2 = c.Height > 700 ? c.Height - 120 : c.Height + 120;
            w.LayoutOffscreen(c.Width, h2);
            Settle(w);
            CheckLog(w, c, Inv($"resize to {c.Width}x{h2}"), problems, report);

            foreach (var p in problems) failures.Add("layout " + c.Name + Loc.T("common.colon") + p);
            report.Add("");
        }
        finally
        {
            w.Close();
            vm.Detach();
        }
        return written;
    }

    private static void CheckPageFits(MainWindow w, string name)
    {
        var page = w.Dashboard.PageScroll;
        var panel = (FillLastPanel)page.Content;
        var card = (FrameworkElement)panel.Children[panel.Children.Count - 1];
        double range = page.ScrollableHeight;
        bool ok = range <= 0.5;
        s_layoutReport.Add(Inv($"{name} | {w.ActualWidth:0}x{w.ActualHeight:0} | page scroll range {range:0.##} | log card {card.ActualHeight:0.##} (min {panel.LastMinHeight:0}) | ")
            + (ok ? "PASS" : "FAIL"));
        if (!ok) s_failures.Add("layout " + name + Loc.T("common.colon") + Inv($"the page scrolls by {range:0.##} DIP at the default window size"));
    }

    /// <summary>Measures the log panel; returns true when the page had to be scrolled (short window).</summary>
    private static bool CheckLog(MainWindow w, LayoutCase c, string step, List<string> problems, List<string> report)
    {
        const double Tol = 0.5;
        var page = w.Dashboard.PageScroll;
        var panel = (FillLastPanel)page.Content;
        var card = (FrameworkElement)panel.Children[panel.Children.Count - 1];
        var list = w.Dashboard.LogList;
        var logSv = FindChild<ScrollViewer>(list) ?? throw new InvalidOperationException("log ScrollViewer not found");
        var found = new List<string>();

        double range = page.ScrollableHeight;
        bool cardAtMin = card.ActualHeight <= panel.LastMinHeight + Tol;
        if (range > Tol && !cardAtMin)
            found.Add(Inv($"page scrolls by {range:0.##} DIP although the log card ({card.ActualHeight:0.##} DIP) is above its minimum height"));
        bool scrolled = range > Tol;
        if (scrolled)
        {
            page.ScrollToEnd();
            Settle(w);
        }
        if (page.ComputedHorizontalScrollBarVisibility == Visibility.Visible || page.ExtentWidth > page.ViewportWidth + Tol)
            found.Add("page scrolls horizontally");
        if (logSv.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
            found.Add("log list shows a horizontal scrollbar");
        if (logSv.VerticalOffset < logSv.ScrollableHeight - Tol)
            found.Add(Inv($"log is not at its end (offset {logSv.VerticalOffset:0.##} of {logSv.ScrollableHeight:0.##})"));

        Rect logVp = Bounds(Presenter(logSv), w), pageVp = Bounds(Presenter(page), w), cardB = Bounds(card, w);
        double statusTop = Bounds(w.StatusBar, w).Top;
        string itemText = "-";
        if (list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is not FrameworkElement item)
            found.Add("newest log line has no container (not realized)");
        else
        {
            Rect ib = Bounds(item, w);
            itemText = Inv($"{ib.Top:0.##}..{ib.Bottom:0.##}");
            if (ib.Top < logVp.Top - Tol || ib.Bottom > logVp.Bottom + Tol) found.Add("newest log line is not fully inside the log viewport");
            if (ib.Bottom > pageVp.Bottom + Tol) found.Add("newest log line is below the page viewport");
            if (ib.Bottom > statusTop + Tol) found.Add("newest log line is behind the status bar");
        }
        if (cardB.Bottom > pageVp.Bottom + Tol || cardB.Bottom > statusTop + Tol) found.Add("bottom of the log card is cut off");

        report.Add(Inv($"{c.Name} | {step} | page {range:0.##}/{page.VerticalOffset:0.##} | log {logSv.VerticalOffset:0.##}/{logSv.ScrollableHeight:0.##} | line {itemText} | log vp {logVp.Bottom:0.##} | card {cardB.Bottom:0.##} | page vp {pageVp.Bottom:0.##} | status {statusTop:0.##} | ")
            + (found.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", found)));
        problems.AddRange(found.Select(f => step + ": " + f));
        return scrolled;
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static FrameworkElement Presenter(ScrollViewer sv) =>
        sv.Template?.FindName("PART_ScrollContentPresenter", sv) as FrameworkElement ?? sv;

    private static Rect Bounds(FrameworkElement el, Visual root) =>
        el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t) return t;
            var r = FindChild<T>(ch);
            if (r != null) return r;
        }
        return null;
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

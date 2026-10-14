using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using VRAirBridge.App.Ui;
using VRAirBridge.App.Ui.Services;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.App.Ui.ViewModels;
using VRAirBridge.App.Ui.Views;
using VRAirBridge.Core;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;
using VRAirBridge.Core.Update;
using VRAirBridge.Core.Util;

namespace VRAirBridge.App;

/// <summary>Command-line options.</summary>
internal sealed class AppOptions
{
    /// <summary>Started by the autostart task: stay in the tray.</summary>
    public bool AutoStart { get; private set; }
    /// <summary>Undo every change this app made to the system, then exit.</summary>
    public bool Uninstall { get; private set; }
    /// <summary>With --uninstall: no dialogs (the uninstaller runs it); the results go to the log only.</summary>
    public bool Quiet { get; private set; }
    /// <summary>"--exit [stop]": closes the running instance and waits for it (the setup uses it); "stop" also stops the hotspot.</summary>
    public bool Exit { get; private set; }
    public bool ExitStop { get; private set; }
    /// <summary>Print diagnostics and exit (optional file path).</summary>
    public bool Diag { get; private set; }
    public string? DiagPath { get; private set; }
    /// <summary>Testing: capture the real window to PNG after a delay, then exit (the hotspot is left as it is).</summary>
    public string? ScreenshotPath { get; private set; }
    public int ScreenshotDelaySeconds { get; private set; } = 15;
    /// <summary>Render the UI with mock data to PNGs (every state × language × theme) and exit.</summary>
    public bool UiScreenshot { get; private set; }
    public string? UiScreenshotDir { get; private set; }
    /// <summary>Debugging: run unelevated without offering "restart as administrator".</summary>
    public bool NoElevate { get; private set; }
    /// <summary>Passed by "restart as administrator": wait for the old process to exit first.</summary>
    public int RelaunchFromPid { get; private set; }
    /// <summary>--updated-from: started by the self-update of the given (older) version.</summary>
    public string? UpdatedFrom { get; private set; }
    /// <summary>Print the D-Link driver status and exit (no administrator needed).</summary>
    public bool DriverStatus { get; private set; }
    /// <summary>Install the bundled driver and exit (administrator needed).</summary>
    public bool InstallDriver { get; private set; }
    /// <summary>"--internet on|off": whether hotspot devices may reach the internet.</summary>
    public string? Internet { get; private set; }
    /// <summary>"--lang auto|en|zh-CN|ja|ko": display language for this run (overrides ui.language, not saved).</summary>
    public string? Language { get; private set; }
    /// <summary>Development: "--write-icon path" regenerates app.ico from the logo drawing and exits.</summary>
    public string? WriteIconPath { get; private set; }
    /// <summary>Development: "--ui-demo [scene]" opens the window with mock data to try the UI by hand.</summary>
    public bool UiDemo { get; private set; }
    public string? UiDemoScene { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            switch (a)
            {
                case "--autostart": o.AutoStart = true; break;
                case "--uninstall": case "--revert": o.Uninstall = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--exit": o.Exit = true; o.ExitStop = string.Equals(Next(), "stop", StringComparison.OrdinalIgnoreCase); break;
                case "--diag": o.Diag = true; o.DiagPath = Next(); break;
                case "--screenshot": o.ScreenshotPath = Next(); break;
                case "--screenshot-delay": if (int.TryParse(Next(), out var d)) o.ScreenshotDelaySeconds = Math.Clamp(d, 1, 120); break;
                case "--ui-screenshot": o.UiScreenshot = true; o.UiScreenshotDir = Next(); break;
                case "--no-elevate": o.NoElevate = true; break;
                case "--relaunch-from": if (int.TryParse(Next(), out var pid)) o.RelaunchFromPid = pid; break;
                case UpdateInstaller.UpdatedFromArg: o.UpdatedFrom = Next(); break;
                case "--driver-status": o.DriverStatus = true; break;
                case "--install-driver": o.InstallDriver = true; break;
                case "--internet": o.Internet = Next() ?? ""; break;
                case "--lang": case "--language": o.Language = Next() ?? ""; break;
                case "--write-icon": o.WriteIconPath = Next() ?? ""; break;
                case "--ui-demo": o.UiDemo = true; o.UiDemoScene = Next(); break;
            }
        }
        return o;
    }
}

internal static class Program
{
    public static string ExePath { get; private set; } = "";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static EventWaitHandle? _internetOn, _internetOff;
    private static EventWaitHandle? _exitKeep, _exitStop;
    private const string ShowEvent = @"\VRAirBridgeHotspot.Show";
    private const string ExitKeepEvent = @"\VRAirBridgeHotspot.Exit.Keep";
    private const string ExitStopEvent = @"\VRAirBridgeHotspot.Exit.Stop";
    private const string InternetOnEvent = @"\VRAirBridgeHotspot.Internet.On";
    private const string InternetOffEvent = @"\VRAirBridgeHotspot.Internet.Off";
    private const string MutexName = @"\VRAirBridgeHotspot.SingleInstance";

    private static string AppName => Loc.T("app.name");
    private static string YesNo(bool b) => Loc.T(b ? "common.yes" : "common.no");

    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VRAirBridgeHotspot.exe");
        var opts = AppOptions.Parse(args);
        bool isAdmin = AdminUtil.IsAdministrator();

        // Display language: --lang, then ui.language from config.json, then the Windows display language.
        // The UI screenshot and demo modes never read config.json (they show mock data only).
        bool langFromCli = ApplyCliLanguage(opts.Language);
        var early = opts.UiScreenshot || opts.UiDemo ? null : ReadUiPreferences();
        if (!langFromCli) Loc.SetLanguage(early?.Language);

        if (opts.UiScreenshot) return RunUiScreenshot(opts);
        if (opts.UiDemo) return RunUiDemo(opts);
        if (opts.WriteIconPath != null) return RunWriteIcon(opts.WriteIconPath);
        if (opts.Exit) return RunExit(opts.ExitStop);
        if (opts.Diag) return RunDiag(opts);
        if (opts.DriverStatus) return RunDriverStatus(isAdmin);
        if (opts.InstallDriver) return RunInstallDriver(isAdmin);
        if (opts.Internet != null) return RunInternet(opts.Internet, isAdmin);

        var app = new AirBridgeApplication(early?.Theme);
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportCrash(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ReportCrash(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Debug(Loc.T("ui.logline.unobserved", e.Exception.GetBaseException().Message));
            e.SetObserved();
        };

        if (opts.RelaunchFromPid > 0)
        {
            // self-update: tell the old version that this one runs, then wait until it has exited
            if (opts.UpdatedFrom != null) UpdateInstaller.SignalReady(opts.RelaunchFromPid);
            try { using var old = Process.GetProcessById(opts.RelaunchFromPid); old.WaitForExit(20000); } catch { }
        }

        // Single instance: if it is already running, bring that window up.
        bool acquired = AcquireSingleInstance(out bool alreadyRunning);
        // relaunch: the previous process may still be closing, so keep trying for a while before giving up
        for (int i = 0; !acquired && alreadyRunning && opts.RelaunchFromPid > 0 && i < 60; i++)
        {
            Thread.Sleep(250);
            acquired = AcquireSingleInstance(out alreadyRunning);
        }
        if (!acquired && alreadyRunning)
        {
            if (opts.Uninstall)
            {
                if (opts.Quiet) return 1;
                DialogWindow.ShowModal(DialogVm.Notice(Loc.T("dialog.uninstallRunning", AppName), DialogKind.Info, AppName));
            }
            else if (!SignalExistingInstance())
                DialogWindow.ShowModal(DialogVm.Notice(Loc.T("dialog.alreadyRunning", AppName), DialogKind.Info, AppName));
            return 0;
        }

        var store = new SettingsStore();
        var migrated = MigrateLegacyData(store);
        Logger.Initialize(store.LogDirectory);
        LogBuffer.Attach();
        var settings = store.Load();
        if (!langFromCli) Loc.SetLanguage(settings.Ui.Language);
        ThemeManager.SetSetting(settings.Ui.Theme);
        Logger.Info(Loc.T("ui.logline.started", AppName, MainViewModel.AppVersion, YesNo(isAdmin), store.DataDirectory));
        LogMigration(migrated, store);
        if (!isAdmin) Logger.Warn(Loc.T("ui.logline.notAdmin"));
        else InstallInfo.SyncDisplayVersion(ExePath, AppInfo.Version);

        using var wd = new Watchdog(store, settings, ExePath, isAdmin);
        wd.EnsureInitialConfig();

        if (opts.Uninstall)
        {
            int code = RunUninstall(wd, isAdmin, opts.Quiet);
            _mutex?.ReleaseMutex();
            return code;
        }

        var ctrl = new AppController(wd, store, settings, opts, isAdmin, ExePath);
        ctrl.StartListeners(_showEvent, _internetOn, _internetOff, _exitKeep, _exitStop);
        if (opts.UpdatedFrom != null)
        {
            Logger.Info(Loc.T("ui.logline.updated", opts.UpdatedFrom, MainViewModel.AppVersion));
            ctrl.Vm.Toasts.Show(Loc.T("ui.update.updated", AppInfo.DisplayVersion), Ui.Tone.Good);
            _ = UpdateInstaller.CleanupLeftoversAsync(ExePath);
        }
        wd.Start(settings.StartHotspotOnLaunch);
        app.Run();
        Logger.Info(Loc.T("ui.logline.exited"));
        try { _mutex?.ReleaseMutex(); } catch { }
        return 0;
    }

    // ------------------------------------------------------------------ language / preferences

    /// <summary>Applies --lang; returns true when it chose a language. An unknown code prints a hint and falls back to auto.</summary>
    private static bool ApplyCliLanguage(string? code)
    {
        if (code == null) return false;
        if (code.Trim().Equals(Loc.Auto, StringComparison.OrdinalIgnoreCase))
        {
            Loc.SetLanguage(Loc.Auto);
            return true;
        }
        var lang = Loc.Normalize(code);
        if (lang == null)
        {
            Loc.SetLanguage(Loc.Auto);
            WriteLine(Loc.T("cli.badLanguage", code));
            return false;
        }
        Loc.SetLanguage(lang);
        return true;
    }

    private sealed record UiPreferences(string? Language, string? Theme);

    /// <summary>
    /// Reads only ui.language and ui.theme from config.json, without the side effects of <see cref="SettingsStore.Load"/>
    /// (no backup of a malformed file, no logging). Nothing else from the file is kept.
    /// </summary>
    private static UiPreferences? ReadUiPreferences()
    {
        try
        {
            var store = new SettingsStore();
            var path = File.Exists(store.ConfigPath) ? store.ConfigPath : OldConfigPath(store);
            if (path == null) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var ui = Find(doc.RootElement, "ui");
            if (ui is not { ValueKind: JsonValueKind.Object } u) return null;
            return new UiPreferences(Str(Find(u, "language")), Str(Find(u, "theme")));
        }
        catch { return null; }

        static JsonElement? Find(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }
        static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    // ------------------------------------------------------------------ data folder

    private static string ExeDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>config.json of a version up to 1.0.2 that has not been moved to the data folder yet, or null.</summary>
    private static string? OldConfigPath(SettingsStore store) =>
        LegacyDataMigration.FindOldConfig(store.DataDirectory, LegacyDataMigration.Candidates(ExeDir, RecordedExePaths));

    private static List<string>? _recordedExePaths;

    /// <summary>Where the autostart task and the firewall rules say the exe is (looked up once, and only when needed).</summary>
    private static IEnumerable<string> RecordedExePaths()
    {
        if (_recordedExePaths == null)
        {
            var paths = new List<string>();
            try { if (AutoStartManager.IsEnabled(out var p) && p != null) paths.Add(p); } catch { }
            try { paths.AddRange(FirewallManager.RecordedExePaths()); } catch { }
            _recordedExePaths = paths;
        }
        return _recordedExePaths;
    }

    /// <summary>Moves what an older version left next to the exe into the data folder. Call before the logger starts.</summary>
    private static LegacyDataMigration.Result? MigrateLegacyData(SettingsStore store)
    {
        try
        {
            IReadOnlyList<string> driverFiles;
            try { driverFiles = DriverPackage.FromAssembly(typeof(Program).Assembly).Manifest.Files.Select(f => f.Name).ToList(); }
            catch { driverFiles = Array.Empty<string>(); }
            return LegacyDataMigration.Run(store.DataDirectory, LegacyDataMigration.Candidates(ExeDir, RecordedExePaths), driverFiles);
        }
        catch { return null; }
    }

    private static void LogMigration(LegacyDataMigration.Result? r, SettingsStore store)
    {
        if (r == null) return;
        Logger.Info(Loc.T("settings.migrated", r.From, store.DataDirectory, r.Moved));
        foreach (var f in r.Failed) Logger.Warn(Loc.T("settings.migrateFailed", f, r.From));
    }

    // ------------------------------------------------------------------ single instance

    private static bool AcquireSingleInstance(out bool alreadyRunning)
    {
        alreadyRunning = false;
        foreach (var scope in new[] { "Global", "Local" })
        {
            try
            {
                var m = new Mutex(true, scope + MutexName, out bool created);
                if (!created)
                {
                    m.Dispose();
                    alreadyRunning = true;
                    return false;
                }
                _mutex = m;
                try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, scope + ShowEvent); } catch { }
                try
                {
                    _internetOn = new EventWaitHandle(false, EventResetMode.AutoReset, scope + InternetOnEvent);
                    _internetOff = new EventWaitHandle(false, EventResetMode.AutoReset, scope + InternetOffEvent);
                }
                catch { _internetOn = _internetOff = null; }
                try
                {
                    _exitKeep = new EventWaitHandle(false, EventResetMode.AutoReset, scope + ExitKeepEvent);
                    _exitStop = new EventWaitHandle(false, EventResetMode.AutoReset, scope + ExitStopEvent);
                }
                catch { _exitKeep = _exitStop = null; }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Created by another (elevated) instance that this unelevated one cannot open: it is running.
                alreadyRunning = true;
                return false;
            }
            catch { /* Global unavailable: try Local */ }
        }
        return true;
    }

    private static bool SignalExistingInstance() => SetEvent(ShowEvent);

    /// <summary>Sets a named event of the running instance; false when it does not exist (or cannot be opened).</summary>
    private static bool SetEvent(string name)
    {
        foreach (var scope in new[] { "Global", "Local" })
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(scope + name, out var ev))
                {
                    using (ev) ev.Set();
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>Tells a running instance to switch internet access. running = an instance exists (reachable or not).</summary>
    private static bool SignalInternet(bool allow, out bool running)
    {
        running = IsInstanceRunning();
        foreach (var scope in new[] { "Global", "Local" })
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(scope + (allow ? InternetOnEvent : InternetOffEvent), out var ev))
                {
                    using (ev) ev.Set();
                    running = true;
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { running = true; }
            catch { }
        }
        return false;
    }

    private static bool IsInstanceRunning()
    {
        foreach (var scope in new[] { "Global", "Local" })
        {
            try { if (Mutex.TryOpenExisting(scope + MutexName, out var m)) { m.Dispose(); return true; } }
            catch (UnauthorizedAccessException) { return true; }
            catch { }
        }
        return false;
    }

    // ------------------------------------------------------------------ console output

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private static bool _consoleChecked, _useConsoleOut;

    /// <summary>
    /// This is a GUI program: when stdout is redirected (pipe / file) write UTF-8 to it directly;
    /// when started from cmd / PowerShell there is no stdout, so attach to the parent console.
    /// </summary>
    private static void WriteOut(string text)
    {
        try
        {
            if (!_consoleChecked)
            {
                _consoleChecked = true;
                var h = GetStdHandle(-11);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                    _useConsoleOut = AttachConsole(-1);
            }
            if (_useConsoleOut)
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Out.Write(text);
                Console.Out.Flush();
                return;
            }
            using var stdout = Console.OpenStandardOutput();
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch { }
    }

    private static void WriteLine(string line) => WriteOut(line + Environment.NewLine);

    // ------------------------------------------------------------------ other modes

    /// <summary>--ui-screenshot [dir]: mock data only; never reads config.json and never takes the single-instance lock.</summary>
    private static int RunUiScreenshot(AppOptions opts)
    {
        var exeDir = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
        var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(opts.UiScreenshotDir) ? Path.Combine(exeDir, "ui-screenshots") : opts.UiScreenshotDir);
        var lang = Loc.Current;
        WriteLine(Loc.T("cli.uiScreenshotStart", dir));
        int written;
        List<string> failures;
        try
        {
            _ = new AirBridgeApplication(ThemeManager.DarkSetting);
            (written, failures) = ScreenshotRenderer.RenderAll(dir, ExePath);
        }
        catch (Exception ex)
        {
            Loc.SetLanguage(lang);
            WriteLine(Loc.T("cli.screenshotFailed", ex.Message));
            return 1;
        }
        Loc.SetLanguage(lang);
        foreach (var f in failures) WriteLine(Loc.T("cli.screenshotFailed", f));
        WriteLine(Loc.T("cli.screenshotDone", written, dir));
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>--ui-demo [scene]: mock data only, like --ui-screenshot; no single-instance lock, so it runs beside the real app.</summary>
    private static int RunUiDemo(AppOptions opts)
    {
        try
        {
            ScreenshotRenderer.RunDemo(new AirBridgeApplication(ThemeManager.SystemSetting), opts.UiDemoScene, ExePath);
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>--write-icon path: app.ico from <see cref="Ui.Controls.AppLogo"/> (run after changing the logo, then rebuild).</summary>
    private static int RunWriteIcon(string path)
    {
        if (path.Length == 0)
        {
            WriteLine("--write-icon <path to .ico>");
            return 2;
        }
        try
        {
            path = Path.GetFullPath(path);
            Ui.Controls.AppLogo.WriteIco(path);
            WriteLine(path);
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>--driver-status: read-only, no administrator needed. Exit code 0 = installed, 1 = plugged in without a driver, 2 = not plugged in, 3 = query failed.</summary>
    private static int RunDriverStatus(bool isAdmin)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("cli.driverStatusTitle", AppName));
        sb.AppendLine(Loc.T("cli.admin", YesNo(isAdmin)));
        int code;
        try
        {
            var st = DriverInstaller.GetState(detailed: true);
            foreach (var l in st.DescribeLines()) sb.AppendLine(l);
            code = st.IsInstalled ? 0 : st.DonglePresent ? 1 : 2;
        }
        catch (Exception ex)
        {
            sb.AppendLine(Loc.T("cli.driverStatusFailed", ex.Message));
            code = 3;
        }
        try
        {
            var problems = DriverPackage.FromAssembly(typeof(Program).Assembly).Verify();
            sb.AppendLine(problems.Count == 0 ? Loc.T("cli.verifyOk") : Loc.T("cli.verifyFailed", string.Join(Loc.T("common.listSep"), problems)));
        }
        catch (Exception ex) { sb.AppendLine(Loc.T("cli.verifyError", ex.Message)); }
        sb.AppendLine(Loc.T("cli.exitCode", code));
        WriteOut(sb.ToString());
        return code;
    }

    private sealed class LineProgress : IProgress<string>
    {
        public void Report(string value) => WriteLine("  " + value);
    }

    /// <summary>--install-driver: administrator needed. Exit code 0 = success (including "staged" and "restart needed"), 1 = failure.</summary>
    private static int RunInstallDriver(bool isAdmin)
    {
        var store = new SettingsStore();
        var migrated = IsInstanceRunning() ? null : MigrateLegacyData(store);
        try { Logger.Initialize(store.LogDirectory); } catch { }
        LogMigration(migrated, store);
        WriteLine(Loc.T("cli.installTitle", AppName, DriverStrings.BundledVersion));
        if (!isAdmin)
        {
            WriteLine(DriverStrings.CliNeedAdmin);
            return 1;
        }
        // A running instance handles the driver itself too; install anyway, its watchdog sees the result next round.
        if (IsInstanceRunning()) Logger.Info(Loc.T("cli.installWhileRunning"));
        try
        {
            var pkg = DriverPackage.FromAssembly(typeof(Program).Assembly);
            var r = Task.Run(() => DriverInstaller.InstallAsync(pkg, store.DataDirectory, new LineProgress())).GetAwaiter().GetResult();
            WriteLine((r.Ok ? "✔ " : "✖ ") + r.Message);
            if (!r.Ok && r.PnpUtil != null && !string.IsNullOrWhiteSpace(r.PnpUtil.Output))
            {
                WriteLine(Loc.T("ui.logline.pnputil"));
                foreach (var l in r.PnpUtil.Output.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0)) WriteLine("    " + l.Trim());
            }
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Logger.Error(Loc.T("cli.installFailedLog", ex.Message));
            WriteLine(Loc.T("cli.installFailed", ex.Message));
            return 1;
        }
    }

    /// <summary>--internet on|off. Exit code 0 = set, 1 = the app is running but could not be reached, 2 = bad argument.</summary>
    private static int RunInternet(string arg, bool isAdmin)
    {
        bool allow;
        switch (arg.Trim().ToLowerInvariant())
        {
            case "on": case "true": case "1": case "yes": allow = true; break;
            case "off": case "false": case "0": case "no": allow = false; break;
            default: WriteLine(InternetStrings.CliUsage); return 2;
        }
        var setText = allow ? InternetStrings.CliSetOn : InternetStrings.CliSetOff;
        if (SignalInternet(allow, out bool running))
        {
            WriteLine(setText);
            WriteLine(InternetStrings.CliSignalled);
            return 0;
        }
        if (running)
        {
            WriteLine(InternetStrings.CliCannotSignal);
            return 1;
        }
        var store = new SettingsStore();
        var migrated = MigrateLegacyData(store); // nothing runs (checked above)
        try { Logger.Initialize(store.LogDirectory); } catch { }
        LogMigration(migrated, store);
        var s = store.Load();
        s.Internet.Allow = allow;
        store.Save(s);
        Logger.Info(Loc.T("cli.internetLog", InternetStrings.StatusText(allow)));
        WriteLine(setText);
        if (!allow && isAdmin)
        {
            try
            {
                var nat = new NatManager(() => s, () => store.Save(s));
                var go = NetworkInfo.FindGoAdapter(DongleInfo.Find()?.Mac);
                var st = nat.Reconcile(go, null, false, true);
                WriteLine(InternetStrings.CliAppliedDirect + Loc.T("common.colon") + st.Text);
            }
            catch (Exception ex) { WriteLine(InternetStrings.CliNextStart + Loc.T("common.paren", ex.Message)); }
        }
        else WriteLine(InternetStrings.CliNextStart);
        return 0;
    }

    private static int RunDiag(AppOptions opts)
    {
        var store = new SettingsStore();
        // read-only: a config.json that an older version left next to the exe is read where it is
        var old = File.Exists(store.ConfigPath) ? null : OldConfigPath(store);
        AppSettings? s = null;
        try { s = (old == null ? store : new SettingsStore(Path.GetDirectoryName(old))).Load(); } catch { }
        var text = Diagnostics.Collect(s, ExePath);
        var path = opts.DiagPath ?? Path.Combine(store.DataDirectory, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try { File.WriteAllText(path, text, new UTF8Encoding(true)); } catch { }
        WriteOut(text);
        return 0;
    }

    /// <summary>--uninstall [--quiet]: undoes the system changes. Quiet (the uninstaller): no dialogs, the results are logged.</summary>
    private static int RunUninstall(Watchdog wd, bool isAdmin, bool quiet)
    {
        if (!isAdmin)
        {
            if (quiet) Logger.Warn(Loc.T("dialog.uninstallNeedAdmin"));
            else DialogWindow.ShowModal(DialogVm.Notice(Loc.T("dialog.uninstallNeedAdmin"), DialogKind.Warning, AppName));
            return 1;
        }
        if (!quiet && DialogWindow.ShowModal(DialogVm.Confirm(Loc.T("ui.maint.revertConfirm"), DialogKind.Warning, AppName)) != DialogVm.Yes) return 0;
        var report = Task.Run(() => wd.RevertAsync()).GetAwaiter().GetResult();
        if (!quiet) DialogWindow.ShowModal(DialogVm.Report(Loc.T("ui.maint.revertResult"), report, Loc.T("ui.maint.revertFooter", wd.DataDirectory)));
        return 0;
    }

    /// <summary>
    /// --exit [stop]: asks the running instance to exit and waits until it has (the setup and the uninstaller use it).
    /// Exit code 0 = not running or exited, 1 = running but cannot be asked (a version up to 1.0.2), 2 = still running.
    /// </summary>
    private static int RunExit(bool stopHotspot)
    {
        if (!IsInstanceRunning()) return 0;
        if (!SetEvent(stopHotspot ? ExitStopEvent : ExitKeepEvent)) return 1;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(250);
            if (!IsInstanceRunning()) return 0;
        }
        return 2;
    }

    private static int _crashShown;

    private static void ReportCrash(Exception ex)
    {
        try
        {
            Logger.Error(Loc.T("ui.logline.crash", ex.Message));
            Logger.Debug(ex.ToString());
        }
        catch { }
        if (Interlocked.Exchange(ref _crashShown, 1) != 0) return;
        void Show()
        {
            try { DialogWindow.ShowModal(DialogVm.Notice(Loc.T("dialog.crash", ex.Message), DialogKind.Error, Loc.T("dialog.crashTitle"))); }
            catch { }
            finally { Interlocked.Exchange(ref _crashShown, 0); }
        }
        try
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted) { Interlocked.Exchange(ref _crashShown, 0); return; }
            if (disp.CheckAccess()) Show();
            else disp.BeginInvoke(DispatcherPriority.Normal, (Action)Show);
        }
        catch { Interlocked.Exchange(ref _crashShown, 0); }
    }
}

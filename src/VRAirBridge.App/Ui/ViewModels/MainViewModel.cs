using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Services;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.Core;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>
/// View model of the main window, the tray menu and the start page. Ports every field, action and prompt of the
/// old WinForms MainForm. With <c>mock</c> (screenshots) there is no watchdog: every command is a no-op and
/// nothing is saved.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    internal const string RepoUrl = "https://github.com/AlanBacker/VR-AirBridge-Hotspot";

    private readonly Watchdog? _wd;
    private readonly SettingsStore? _store;
    private readonly AppSettings _s;
    private readonly IUiHost _host;
    private readonly string _exePath;
    private readonly bool _mock;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer? _logTimer;
    private AppStatus? _last;
    private bool _uiBusy, _internetBusy, _scanning, _exiting;
    private bool _driverPromptPending, _driverPromptShowing;
    // language forced for this run with --lang ("auto" or a code); shown in the dropdown, never saved
    private string? _langOverride;

    internal MainViewModel(Watchdog? wd, SettingsStore? store, AppSettings s, bool isAdmin, bool canElevate, string exePath, IUiHost host, bool mock,
                           string? languageOverride = null)
    {
        _langOverride = languageOverride;
        _wd = wd;
        _store = store;
        _s = s;
        _host = host;
        _exePath = exePath;
        _mock = mock || wd == null;
        IsAdmin = isAdmin;
        CanElevate = canElevate && !isAdmin;

        Dialogs = new DialogHostVm
        {
            BeforeShow = () => { if (!_host.IsWindowVisible) _host.ShowWindow(); },
            Copy = t => _host.CopyText(t),
        };
        Toasts = new ToastHostVm();

        Languages = new ObservableCollection<LanguageChoice>(new[] { Loc.Auto }.Concat(Loc.Codes).Select(c => new LanguageChoice(c)));
        Themes = new ObservableCollection<Choice>
        {
            new(ThemeManager.SystemSetting, () => Loc.T("ui.opt.themeSystem")),
            new(ThemeManager.DarkSetting, () => Loc.T("ui.opt.themeDark")),
            new(ThemeManager.LightSetting, () => Loc.T("ui.opt.themeLight")),
        };
        Bands = new ObservableCollection<Choice>
        {
            new("5GHz", () => Loc.T("ui.hs.band5")),
            new("2.4GHz", () => Loc.T("ui.hs.band24")),
            new("Auto", () => Loc.T("ui.hs.bandAuto")),
        };
        Auths = new ObservableCollection<Choice>
        {
            new("WPA2", () => Loc.T("ui.hs.authWpa2")),
            new("WPA3Transition", () => Loc.T("ui.hs.authTransition")),
            new("WPA3", () => Loc.T("ui.hs.authWpa3")),
        };

        Metrics = new ObservableCollection<MetricVm>
        {
            new("I.radio"), new("I.activity"), new("I.wifi"), new("I.sliders-horizontal"),
        };
        StatusSegs = new ObservableCollection<StatusSegVm>
        {
            new("driver"), new("dhcp"), new("nat"), new("internet"), new("connected"), new("channel"),
        };
        Steps = new ObservableCollection<StepVm> { new(1), new(2), new(3) };
        DriverTile = new TileVm("I.cpu");
        SettingsTile = new TileVm("I.key-round");
        ConnectTile = new TileVm("I.smartphone");

        SecHotspot = new SectionVm("hotspot", new[] { "ui.sec.hotspot", "ui.hs.ssid", "ui.hs.password", "ui.hs.band", "ui.hs.auth", "ui.hs.apply", "ui.hs.random" });
        SecNetwork = new SectionVm("network", new[] { "ui.sec.network", "ui.net.dhcp", "ui.net.nat", "ui.net.internet", "ui.net.uplink", "ui.net.admin", "internet.checkBox", "ui.net.pool", "ui.net.dns", "ui.net.lease", "ui.net.dhcpMode" });
        SecDriver = new SectionVm("driver", new[] { "ui.sec.driver", "ui.drv.adapter", "ui.drv.connName", "ui.drv.driver", "ui.drv.mode", "ui.drv.status", "ui.drv.bundled", "driver.buttonInstall", "driver.buttonReinstall" });
        SecOptions = new SectionVm("options", new[] { "ui.sec.options", "ui.opt.autostart", "ui.opt.startOnLaunch", "ui.opt.tray", "ui.opt.keep", "ui.opt.appearance", "ui.opt.theme", "ui.opt.themeDark", "ui.opt.themeLight", "ui.opt.language" });
        SecMaint = new SectionVm("maint", new[] { "ui.sec.maint", "ui.maint.optimize", "ui.maint.repair", "ui.maint.beacon", "ui.maint.logs", "ui.maint.diag", "ui.maint.revert", "meta.section" });
        SecAbout = new SectionVm("about", new[] { "ui.sec.about", "ui.about.version", "ui.about.license", "ui.about.repo", "ui.about.dataDir", "ui.about.updates", "ui.update.autoCheck", "ui.update.checkNow" });
        Sections = new ObservableCollection<SectionVm> { SecHotspot, SecNetwork, SecDriver, SecOptions, SecMaint, SecAbout };

        // ------------------------------------------------------------------ commands
        PrimaryCommand = new RelayCommand(() => { if (PrimaryIsStop) _ = StopAsync(); else _ = StartAsync(); });
        StartCommand = new RelayCommand(() => _ = StartAsync());
        RefreshCommand = new RelayCommand(Refresh);
        DocsCommand = new RelayCommand(() => OpenUrl(RepoUrl));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleSidebarCommand = new RelayCommand(() => SidebarOpen = !SidebarOpen);
        ElevateCommand = new RelayCommand(() => _ = ElevateAsync());
        SkipStartCommand = new RelayCommand(SkipStart);
        ShowStartCommand = new RelayCommand(ShowStartPage);
        EditSettingsCommand = new RelayCommand(RequestFocusHotspotSettings);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        RandomPasswordCommand = new RelayCommand(() => { EditPassword = AppSettings.GeneratePassword(12); PasswordRevealed = true; });
        TogglePasswordCommand = new RelayCommand(() => PasswordRevealed = !PasswordRevealed);
        ApplyCommand = new RelayCommand(() => _ = ApplySettingsAsync());
        InstallDriverCommand = new RelayCommand(() => _ = InstallDriverAsync(fromPrompt: false));
        ScanCommand = new RelayCommand(() => _ = ScanChannelsAsync());
        CopyDeviceCommand = new RelayCommand(CopyDevice);
        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogCommand = new RelayCommand(p => CopyLog(p as System.Collections.IList));

        OptimizeCommand = new RelayCommand(() => _ = OptimizeAsync());
        RepairCommand = new RelayCommand(() => _ = RepairAsync());
        BeaconCommand = new RelayCommand(() => _ = BeaconAsync());
        OpenLogsCommand = new RelayCommand(OpenLogs);
        CopyDiagCommand = new RelayCommand(() => _ = CopyDiagAsync());
        RevertCommand = new RelayCommand(() => _ = RevertAsync());
        MetaCheckCommand = new RelayCommand(() => _ = MetaCheckAsync());
        OpenRepoCommand = new RelayCommand(() => OpenUrl(RepoUrl));
        OpenDataDirCommand = new RelayCommand(OpenDataDir);

        TrayStartCommand = new RelayCommand(() => _ = StartAsync());
        TrayStopCommand = new RelayCommand(() => _ = StopAsync());
        ShowWindowCommand = new RelayCommand(() => _host.ShowWindow());
        ToggleInternetCommand = new RelayCommand(() => _ = ToggleInternetAsync(!_s.Internet.Allow));
        ExitKeepCommand = new RelayCommand(() => _ = _host.ExitAsync(stopHotspot: false, ask: true));
        ExitStopCommand = new RelayCommand(() => _ = _host.ExitAsync(stopHotspot: true, ask: false));

        LoadSettingsToUi();
        RebuildStaticRows();

        Loc.LanguageChanged += OnLanguageChangedAnyThread;
        ThemeManager.Changed += OnThemeChanged;

        if (!_mock && _wd != null)
        {
            _wd.StatusChanged += OnStatusAnyThread;
            _wd.Notify += OnNotifyAnyThread;
            _wd.DriverMissing += OnDriverMissingAnyThread;
            RenderLog(LogBuffer.Snapshot());
            LogBuffer.DrainPending();
            _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _logTimer.Tick += (_, _) => FlushLog();
            _logTimer.Start();
            ApplyStatus(_wd.Status);
            _ = SyncAutoStartAsync();
            _ = InitialScanAsync();
            StartUpdateChecks();
        }
    }

    // ================================================================== shared

    public DialogHostVm Dialogs { get; }
    public ToastHostVm Toasts { get; }

    public bool IsAdmin { get; }
    public bool CanElevate { get; }
    public bool ShowAdminBanner => !IsAdmin;
    public string AppName => Loc.T("app.name");
    public string WindowTitle => Loc.T("app.name") + (IsAdmin ? "" : Loc.T("app.notAdminSuffix"));

    internal AppSettings Settings => _s;
    internal bool IsMock => _mock;

    /// <summary>Raised when the view should open the sidebar's hotspot section and focus the name box.</summary>
    internal event Action? FocusHotspotSettingsRequested;

    /// <summary>Raised when the log list was rebuilt (the view scrolls to the end).</summary>
    internal event Action? LogReset;

    private void Save()
    {
        if (_mock) return;
        try
        {
            if (_wd != null) _wd.SaveSettings();
            else _store?.Save(_s);
        }
        catch (Exception ex) { Logger.Warn(Loc.T("ui.opFailed", ex.Message)); }
    }

    private void Post(Action a)
    {
        if (_ui.CheckAccess()) a();
        else _ui.BeginInvoke(a);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn(Loc.T("ui.opFailed", ex.Message)); }
    }

    internal static string AppVersion => AppInfo.Version;

    // ================================================================== header

    private string _stateText = "";
    private Tone _stateTone = Tone.Dim;
    private bool _primaryIsStop, _canStart, _canStop, _isBusy, _canBeacon = true;

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public Tone StateTone { get => _stateTone; private set => Set(ref _stateTone, value); }

    public bool PrimaryIsStop
    {
        get => _primaryIsStop;
        private set
        {
            if (!Set(ref _primaryIsStop, value)) return;
            OnPropertyChanged(nameof(PrimaryText));
            OnPropertyChanged(nameof(PrimaryIcon));
            OnPropertyChanged(nameof(PrimaryTip));
            OnPropertyChanged(nameof(CanPrimary));
        }
    }

    public string PrimaryText => Loc.T(PrimaryIsStop ? "ui.header.stop" : "ui.header.start");
    public string PrimaryIcon => PrimaryIsStop ? "I.square" : "I.play";
    public string PrimaryTip => Loc.T(PrimaryIsStop ? "ui.header.stopTip" : "ui.header.startTip");
    public bool CanStart { get => _canStart; private set { if (Set(ref _canStart, value)) OnPropertyChanged(nameof(CanPrimary)); } }
    public bool CanStop { get => _canStop; private set { if (Set(ref _canStop, value)) OnPropertyChanged(nameof(CanPrimary)); } }
    public bool CanPrimary => PrimaryIsStop ? CanStop : CanStart;

    /// <summary>An operation is running (watchdog busy or a UI operation): most actions are disabled.</summary>
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanOps)); } }
    public bool CanOps => !_isBusy;
    public bool CanBeacon { get => _canBeacon; private set => Set(ref _canBeacon, value); }

    public ICommand PrimaryCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DocsCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand ElevateCommand { get; }

    public string ThemeIcon => ThemeManager.IsLight ? "I.moon" : "I.sun";
    public string ThemeTip => Loc.T(ThemeManager.IsLight ? "ui.header.themeToDark" : "ui.header.themeToLight");

    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(ThemeIcon));
        OnPropertyChanged(nameof(ThemeTip));
    }

    private void ToggleTheme()
    {
        var next = ThemeManager.IsLight ? ThemeManager.DarkSetting : ThemeManager.LightSetting;
        SelectedTheme = Themes.FirstOrDefault(t => t.Value == next);
    }

    private void Refresh()
    {
        _wd?.Wake();
        Toasts.Show(Loc.T("ui.toast.refreshed"), Tone.Accent);
    }

    private bool _sidebarOpen = true;

    public bool SidebarOpen
    {
        get => _sidebarOpen;
        set { if (Set(ref _sidebarOpen, value)) OnPropertyChanged(nameof(SidebarTip)); }
    }

    public string SidebarTip => Loc.T(SidebarOpen ? "ui.side.collapse" : "ui.side.expand");

    // ------------------------------------------------------------------ language / theme

    public ObservableCollection<LanguageChoice> Languages { get; }
    private LanguageChoice? _selectedLanguage;

    public LanguageChoice? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value == null || !Set(ref _selectedLanguage, value) || _quiet) return;
            ChangeLanguage(value.Code);
        }
    }

    public ObservableCollection<Choice> Themes { get; }
    private Choice? _selectedTheme;

    public Choice? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value == null || !Set(ref _selectedTheme, value) || _quiet) return;
            _s.Ui.Theme = value.Value;
            Save();
            ThemeManager.SetSetting(value.Value);
        }
    }

    /// <summary>Short code of the language in use ("EN", "ZH", "JA", "KO"), shown by the compact language button.</summary>
    public string LanguageShort => Loc.Current.Split('-')[0].ToUpperInvariant();

    /// <summary>Tooltip of the language button: names the language in use.</summary>
    public string LanguageTip => Loc.T("ui.header.languageTip", Loc.NativeName(Loc.Current));

    private void ChangeLanguage(string code)
    {
        _langOverride = null; // a choice made in the UI replaces the --lang override and is saved
        _s.Ui.Language = code;
        Save();
        Loc.SetLanguage(Loc.ResolveSetting(code));
        if (!_mock) Logger.Info(Loc.T("ui.logline.languageChanged", Loc.NativeName(Loc.Current)));
    }

    private void OnLanguageChangedAnyThread() => Post(OnLanguageChanged);

    private void OnLanguageChanged()
    {
        foreach (var l in Languages) l.Refresh();
        foreach (var c in Themes.Concat(Bands).Concat(Auths)) c.Refresh();
        _pinItemsKey = "";
        RebuildStaticRows();
        SettingsTile.RefreshSecretTexts();
        if (_last != null) ApplyStatus(_last);
        ApplySearch();
        OnAllPropertiesChanged();
        _wd?.Wake();
    }

    // quiet: set a bound property without running its side effect (loading settings, syncing from status)
    private bool _quiet;

    private void Quietly(Action a)
    {
        bool old = _quiet;
        _quiet = true;
        try { a(); }
        finally { _quiet = old; }
    }

    private void LoadSettingsToUi()
    {
        Quietly(() =>
        {
            EditSsid = _s.Ssid;
            EditPassword = _s.Password;
            SelectedBand = Bands.FirstOrDefault(b => string.Equals(b.Value, _s.Band, StringComparison.OrdinalIgnoreCase)) ?? Bands[0];
            SelectedAuth = Auths.FirstOrDefault(b => string.Equals(b.Value, _s.Auth, StringComparison.OrdinalIgnoreCase)) ?? Auths[0];
            OptAutoStart = _s.AutoStart;
            OptStartOnLaunch = _s.StartHotspotOnLaunch;
            OptTray = _s.MinimizeToTray;
            OptKeep = _s.KeepHotspotOnClose;
            InternetAllowed = _s.Internet.Allow;
            var setting = _langOverride ?? _s.Ui.Language;
            var lang = string.IsNullOrWhiteSpace(setting) || setting.Equals(Loc.Auto, StringComparison.OrdinalIgnoreCase)
                ? Loc.Auto : Loc.Normalize(setting) ?? Loc.Auto;
            SelectedLanguage = Languages.FirstOrDefault(l => l.Code == lang) ?? Languages[0];
            SelectedTheme = Themes.FirstOrDefault(t => t.Value == ThemeManager.Normalize(_s.Ui.Theme)) ?? Themes[0];
        });
    }

    // ================================================================== sidebar

    public ObservableCollection<SectionVm> Sections { get; }
    public SectionVm SecHotspot { get; }
    public SectionVm SecNetwork { get; }
    public SectionVm SecDriver { get; }
    public SectionVm SecOptions { get; }
    public SectionVm SecMaint { get; }
    public SectionVm SecAbout { get; }
    public ICommand ClearSearchCommand { get; }

    private string _search = "";
    private bool _noMatch;

    public string SearchText
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            OnPropertyChanged(nameof(HasSearch));
            ApplySearch();
        }
    }

    public bool HasSearch => _search.Length > 0;
    public bool NoMatch { get => _noMatch; private set => Set(ref _noMatch, value); }
    public string NoMatchText => Loc.T("ui.side.noMatch", _search.Trim());

    private void ApplySearch()
    {
        var q = _search.Trim();
        foreach (var sec in Sections) sec.ApplySearch(q);
        NoMatch = q.Length > 0 && Sections.All(s => !s.IsVisible);
        OnPropertyChanged(nameof(NoMatchText));
    }

    public bool IsAdvanced
    {
        get => _s.Ui.Advanced;
        set
        {
            if (_s.Ui.Advanced == value) return;
            _s.Ui.Advanced = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPinSection));
            OnPropertyChanged(nameof(EffectiveDebug));
            RenderLog(null);
        }
    }

    // ------------------------------------------------------------------ hotspot settings

    public ObservableCollection<Choice> Bands { get; }
    public ObservableCollection<Choice> Auths { get; }
    private string _editSsid = "", _editPassword = "", _windowsCurrent = "";
    private bool _pwRevealed;
    private Choice? _band, _auth;

    public string EditSsid { get => _editSsid; set => Set(ref _editSsid, value ?? ""); }
    public string EditPassword { get => _editPassword; set => Set(ref _editPassword, value ?? ""); }

    public bool PasswordRevealed
    {
        get => _pwRevealed;
        set { if (Set(ref _pwRevealed, value)) { OnPropertyChanged(nameof(PasswordToggleTip)); OnPropertyChanged(nameof(PasswordToggleIcon)); } }
    }

    public string PasswordToggleTip => Loc.T(_pwRevealed ? "ui.hs.hide" : "ui.hs.show");
    public string PasswordToggleIcon => _pwRevealed ? "I.eye-off" : "I.eye";
    public Choice? SelectedBand { get => _band; set => Set(ref _band, value); }
    public Choice? SelectedAuth { get => _auth; set => Set(ref _auth, value); }
    public string WindowsCurrentText { get => _windowsCurrent; private set => Set(ref _windowsCurrent, value); }

    public ICommand RandomPasswordCommand { get; }
    public ICommand TogglePasswordCommand { get; }
    public ICommand ApplyCommand { get; }

    private void RequestFocusHotspotSettings()
    {
        SidebarOpen = true;
        if (_search.Length > 0) SearchText = "";
        SecHotspot.IsExpanded = true;
        FocusHotspotSettingsRequested?.Invoke();
    }

    // ------------------------------------------------------------------ network

    private bool _internetAllowed = true;

    public bool InternetAllowed
    {
        get => _internetAllowed;
        set
        {
            if (!Set(ref _internetAllowed, value) || _quiet) return;
            Later(() => _ = ToggleInternetAsync(value));
        }
    }

    public KvList NetRows { get; } = new();
    public KvList PoolRows { get; } = new();

    // ------------------------------------------------------------------ driver

    public KvList DriverRows { get; } = new();
    private string _driverButtonText = "";
    private string? _driverWarning;
    public string DriverButtonText { get => _driverButtonText; private set => Set(ref _driverButtonText, value); }
    public string DriverButtonTip => DriverStrings.ButtonTooltip;
    public string? DriverWarning { get => _driverWarning; private set => Set(ref _driverWarning, value); }
    public ICommand InstallDriverCommand { get; }

    // ------------------------------------------------------------------ options

    private bool _optAutoStart, _optStartOnLaunch, _optTray, _optKeep;

    public bool OptAutoStart
    {
        get => _optAutoStart;
        set
        {
            if (!Set(ref _optAutoStart, value) || _quiet) return;
            Later(() => _ = ToggleAutoStartAsync(value));
        }
    }

    public bool OptStartOnLaunch
    {
        get => _optStartOnLaunch;
        set
        {
            if (!Set(ref _optStartOnLaunch, value) || _quiet) return;
            _s.StartHotspotOnLaunch = value;
            Save();
        }
    }

    public bool OptTray
    {
        get => _optTray;
        set
        {
            if (!Set(ref _optTray, value) || _quiet) return;
            _s.MinimizeToTray = value;
            Save();
        }
    }

    public bool OptKeep
    {
        get => _optKeep;
        set
        {
            if (!Set(ref _optKeep, value) || _quiet) return;
            _s.KeepHotspotOnClose = value;
            Save();
        }
    }

    // ------------------------------------------------------------------ maintenance / about

    public ICommand OptimizeCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand BeaconCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand CopyDiagCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand MetaCheckCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand OpenDataDirCommand { get; }

    private string? _metaText;
    private bool _metaChecking;
    public string? MetaText { get => _metaText; private set => Set(ref _metaText, value); }
    public bool MetaChecking { get => _metaChecking; private set { if (Set(ref _metaChecking, value)) OnPropertyChanged(nameof(MetaButtonText)); } }
    public string MetaButtonText => Loc.T(_metaChecking ? "ui.maint.metaChecking" : "ui.maint.metaCheck");

    public string VersionText => AppVersion;
    public string RepoText => RepoUrl.Replace("https://", "");
    public string DataDirectoryText => _mock ? @"C:\Tools\VRAirBridgeHotspot" : _store?.DataDirectory ?? _wd?.DataDirectory ?? "—";
    public string BundledDriverText => Loc.T("common.nameParen", DriverStrings.BundledVersion, DriverStrings.BundledDate);

    // ================================================================== start page

    public ObservableCollection<StepVm> Steps { get; }
    public TileVm DriverTile { get; }
    public TileVm SettingsTile { get; }
    public TileVm ConnectTile { get; }
    public ICommand SkipStartCommand { get; }
    public ICommand ShowStartCommand { get; }
    public ICommand EditSettingsCommand { get; }

    private bool _showStart, _forceStart, _startDismissed;

    public bool ShowStart { get => _showStart; private set => Set(ref _showStart, value); }

    private void SkipStart()
    {
        _forceStart = false;
        _startDismissed = true;
        if (!_s.Ui.SetupDone)
        {
            _s.Ui.SetupDone = true;
            Save();
        }
        UpdateStartPage(_last);
    }

    private void ShowStartPage()
    {
        _forceStart = true;
        _startDismissed = false;
        UpdateStartPage(_last);
    }

    // ================================================================== tray

    private bool _trayCanStart = true, _trayCanStop;
    public bool TrayCanStart { get => _trayCanStart; private set => Set(ref _trayCanStart, value); }
    public bool TrayCanStop { get => _trayCanStop; private set => Set(ref _trayCanStop, value); }
    public ICommand TrayStartCommand { get; }
    public ICommand TrayStopCommand { get; }
    public ICommand ShowWindowCommand { get; }
    public ICommand ToggleInternetCommand { get; }
    public ICommand ExitKeepCommand { get; }
    public ICommand ExitStopCommand { get; }

    // ================================================================== status bar

    public ObservableCollection<StatusSegVm> StatusSegs { get; }

    // ================================================================== lifetime

    /// <summary>Exit in progress: headline shows "exiting", the log timer stops and prompts are suppressed.</summary>
    internal void SetExiting(bool stopHotspot)
    {
        _exiting = true;
        _uiBusy = true;
        _logTimer?.Stop();
        _updateTimer?.Stop();
        Headline = Loc.T(stopHotspot ? "ui.exitingStop" : "ui.exiting");
        IsBusy = true;
        CanStart = CanStop = false;
    }

    internal void Detach()
    {
        _logTimer?.Stop();
        StopUpdateChecks();
        Loc.LanguageChanged -= OnLanguageChangedAnyThread;
        ThemeManager.Changed -= OnThemeChanged;
        if (_wd != null)
        {
            _wd.StatusChanged -= OnStatusAnyThread;
            _wd.Notify -= OnNotifyAnyThread;
            _wd.DriverMissing -= OnDriverMissingAnyThread;
        }
    }
}

using System.Collections.ObjectModel;
using VRAirBridge.Core;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>Status → view state (port of MainForm.ApplyStatus / ApplyChannel / UpdateClients / log).</summary>
public sealed partial class MainViewModel
{
    private const int MaxLog = 1500;
    private OverallState? _prevState;

    // ================================================================== status card

    private string _headline = "", _detail = "";
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public ObservableCollection<string> HintItems { get; } = new();
    public bool HasHints => HintItems.Count > 0;

    // ================================================================== hotspot card

    private string _hotspotTitle = "", _hotspotSubtitle = "", _fullText = "";
    private Tone _fullTone = Tone.Dim;
    private bool _hotspotDetailsOpen;

    public string HotspotTitle { get => _hotspotTitle; private set => Set(ref _hotspotTitle, value); }
    public string HotspotSubtitle { get => _hotspotSubtitle; private set => Set(ref _hotspotSubtitle, value); }
    public string FullText { get => _fullText; private set => Set(ref _fullText, value); }
    public Tone FullTone { get => _fullTone; private set => Set(ref _fullTone, value); }
    public ObservableCollection<MetricVm> Metrics { get; }
    public KvList HotspotRows { get; } = new();
    public bool HotspotDetailsOpen { get => _hotspotDetailsOpen; set => Set(ref _hotspotDetailsOpen, value); }

    // ================================================================== channel card

    public KvList ChannelRows { get; } = new();
    public ObservableCollection<PinItemVm> PinItems { get; } = new();
    private PinItemVm? _selectedPin;
    private string _pinItemsKey = "", _pinText = "";
    private Tone _pinTone = Tone.Normal;
    private bool _pinDropOpen, _canScan = true;

    public PinItemVm? SelectedPin
    {
        get => _selectedPin;
        set
        {
            if (!Set(ref _selectedPin, value) || _quiet || value == null) return;
            Later(() => _ = PinChangedAsync(value));
        }
    }

    /// <summary>Bound to the dropdown's IsDropDownOpen: the items are not rebuilt while it is open.</summary>
    public bool PinDropDownOpen { get => _pinDropOpen; set => Set(ref _pinDropOpen, value); }

    public string PinText { get => _pinText; private set => Set(ref _pinText, value); }
    public Tone PinTone { get => _pinTone; private set => Set(ref _pinTone, value); }
    public bool ShowPinSection => IsAdvanced || !string.IsNullOrWhiteSpace(_s.Channel.PinSsid);
    public bool CanScan { get => _canScan; private set => Set(ref _canScan, value); }
    public System.Windows.Input.ICommand ScanCommand { get; }

    // ================================================================== devices

    public ObservableCollection<DeviceRowVm> Devices { get; } = new();
    private DeviceRowVm? _selectedDevice;
    private string _devicesSummary = "", _devicesSig = "\u0000";
    public DeviceRowVm? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public string DevicesSummary { get => _devicesSummary; private set => Set(ref _devicesSummary, value); }
    public bool HasDevices => Devices.Count > 0;
    public string DevicesEmptyHint => Loc.T("ui.devices.emptyHint", string.IsNullOrWhiteSpace(_last?.Hotspot?.Ssid) ? _s.Ssid : _last!.Hotspot!.Ssid);
    public System.Windows.Input.ICommand CopyDeviceCommand { get; }

    // ================================================================== log

    private ObservableCollection<LogLineVm> _logLines = new();
    private DateTime _logClearedAt = DateTime.MinValue;

    public ObservableCollection<LogLineVm> LogLines { get => _logLines; private set => Set(ref _logLines, value); }
    public bool HasLog => _logLines.Count > 0;

    public bool ShowDebugLog
    {
        get => _s.ShowDebugLog;
        set
        {
            if (_s.ShowDebugLog == value) return;
            _s.ShowDebugLog = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveDebug));
            RenderLog(null);
        }
    }

    /// <summary>Debug lines are shown only in Advanced mode with "show debug messages" on.</summary>
    public bool EffectiveDebug => IsAdvanced && _s.ShowDebugLog;

    public System.Windows.Input.ICommand ClearLogCommand { get; }
    public System.Windows.Input.ICommand CopyLogCommand { get; }

    // ================================================================== watchdog events (any thread)

    private void OnStatusAnyThread(AppStatus st) => Post(() => ApplyStatus(st));

    private void OnNotifyAnyThread(Notification n) => Post(() => _host.Balloon(n));

    private void OnDriverMissingAnyThread(DriverState d) => Post(() =>
    {
        _driverPromptPending = true;
        TryPromptDriver(d);
    });

    /// <summary>Runs after the current input / binding update has finished.</summary>
    private void Later(Action a) => _ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, a);

    // ================================================================== ApplyStatus

    internal void ApplyStatus(AppStatus st)
    {
        _last = st;
        if (_exiting) return;
        var prev = _prevState;
        _prevState = st.State;
        if (!_mock && prev != null && prev != OverallState.Running && st.State == OverallState.Running)
            Toasts.Show(Loc.T("ui.toast.started"), Tone.Good);

        Headline = st.Headline;
        var detail = st.Detail;
        if (st.BusyText != null) detail = "⏳ " + st.BusyText + "  " + detail;
        Detail = detail;

        (StateText, StateTone) = st.State switch
        {
            OverallState.Running => (Loc.T("ui.state.running"), Tone.Good),
            OverallState.Starting => (Loc.T("ui.state.starting"), Tone.Accent),
            OverallState.Retrying => (Loc.T("ui.state.problem"), Tone.Warn),
            OverallState.Error => (Loc.T("ui.state.problem"), Tone.Bad),
            _ => (Loc.T("ui.state.stopped"), Tone.Dim),
        };

        bool busy = st.BusyText != null || _uiBusy;
        bool running = st.State == OverallState.Running;
        IsBusy = busy;
        PrimaryIsStop = st.DesiredOn || running || st.State == OverallState.Retrying;
        CanStart = !busy && !(st.DesiredOn && running);
        CanStop = !busy && (st.DesiredOn || running || st.State == OverallState.Retrying);
        CanBeacon = !busy && !st.BeaconRunning;
        TrayCanStart = !(st.DesiredOn && running);
        TrayCanStop = st.DesiredOn || running;

        // the not-admin banner above the page already says this: don't repeat it in the hotspot card
        var notAdminHint = ShowAdminBanner ? Loc.T("watchdog.notRunningAsAdministratorBuilt") : null;
        var hints = st.Hints.Where(h => h != notAdminHint).ToList();
        if (!HintItems.SequenceEqual(hints))
        {
            HintItems.Clear();
            foreach (var h in hints) HintItems.Add(h);
            OnPropertyChanged(nameof(HasHints));
        }

        ApplyDriver(st);
        ApplyHotspot(st);
        ApplyNetwork(st);
        ApplyChannel(st);

        var hs = st.Hotspot;
        if (hs != null) WindowsCurrentText = Loc.T("ui.hs.windowsCurrent", hs.Ssid, hs.BandText, hs.AuthText);

        UpdateClients(st.Clients);
        UpdateStatusBar(st);
        UpdateStartPage(st);
        _host.UpdateTray(st);
        TryPromptDriver(st.Driver);
    }

    private void ApplyDriver(AppStatus st)
    {
        var d = st.Dongle;
        var drv = st.Driver;
        var desc = d?.Description ?? (drv.Kind == DriverStateKind.PresentNoDriver
            ? Loc.T("ui.drv.pluggedNoDriver", drv.DeviceName ?? "802.11ax WLAN Adapter")
            : Loc.T("ui.drv.notDetected"));
        DriverButtonText = drv.IsInstalled ? DriverStrings.ButtonReinstall : DriverStrings.ButtonInstall;
        string? drvValue = d != null
            ? $"{d.DriverText}  {d.DriverProvider}" + (drv.Kind == DriverStateKind.DriverOtherVersion ? Loc.T("ui.drv.notBundled", DriverStrings.BundledVersion) : "")
            : drv.Kind == DriverStateKind.PresentNoDriver ? drv.ShortText : null;
        var drvTone = drv.Kind switch
        {
            DriverStateKind.DriverOtherVersion => Tone.Warn,
            DriverStateKind.PresentNoDriver => Tone.Bad,
            _ => Tone.Normal,
        };
        DriverRows.Apply(new KvSpec[]
        {
            new("desc", Loc.T("ui.drv.adapter"), desc, d == null ? Tone.Bad : Tone.Normal),
            new("name", Loc.T("ui.drv.connName"), d?.Name),
            new("mac", Loc.T("ui.drv.mac"), d?.Mac),
            new("driver", Loc.T("ui.drv.driver"), drvValue, drvTone, drv.Kind == DriverStateKind.PresentNoDriver ? DriverStrings.InlineWarning : drv.ShortText),
            new("mode", Loc.T("ui.drv.mode"), d?.WirelessMode, d?.WirelessMode != null && !d.WirelessMode.Contains("ax") ? Tone.Warn : Tone.Normal),
            new("status", Loc.T("ui.drv.status"),
                d == null ? Loc.T(drv.DonglePresent ? "ui.drv.noDriverUnusable" : "ui.drv.notPlugged") : d.StatusText,
                d == null || d.IsDisabled ? Tone.Bad : Tone.Good),
            new("bundled", Loc.T("ui.drv.bundled"), BundledDriverText, Tone.Dim),
        });
        DriverWarning = drv.Kind == DriverStateKind.PresentNoDriver ? DriverStrings.InlineWarning : null;
    }

    private void ApplyHotspot(AppStatus st)
    {
        var go = st.Go;
        var hs = st.Hotspot;
        bool on = hs?.State == HotspotState.On;

        HotspotTitle = !string.IsNullOrWhiteSpace(hs?.Ssid) ? hs!.Ssid! : _s.Ssid;
        HotspotSubtitle = hs != null && on ? $"{hs.BandText} · {hs.AuthText}" : Loc.T("ui.dash.hotspotOff");

        string beaconNote;
        var b0 = on ? st.Beacon : null; // 热点已关：不再显示上一次的信标结果（满血/指标）
        if (b0 != null)
        {
            var i = b0.Info;
            SetMetric(0, "ui.dash.channel", Loc.T("ui.dash.chFreq", i.Channel, i.FreqMHz), i.FreqMHz > 5000 ? Tone.Good : Tone.Warn);
            SetMetric(1, "ui.dash.width", Loc.T("ui.dash.mhz", i.WidthMHz), i.WidthMHz >= 80 ? Tone.Good : Tone.Warn);
            SetMetric(2, "ui.dash.phy", i.Phy, i.Phy == "802.11ax" ? Tone.Good : Tone.Warn);
            SetMetric(3, "ui.dash.streams", i.Streams.ToString(), i.Streams >= 2 ? Tone.Good : Tone.Warn);
            FullText = Loc.T(i.IsFull ? "ui.dash.fullYes" : "ui.dash.fullNo");
            FullTone = i.IsFull ? Tone.Good : Tone.Warn;
            beaconNote = Loc.T("ui.dash.beaconNote", b0.Time, b0.ScannerName, b0.Rssi)
                         + (b0.SameChannelOthers.Count > 0 ? Loc.T("ui.dash.beaconSameChannel", b0.SameChannelOthers.Count) : "");
        }
        else
        {
            var goCh = on ? st.Channel?.GoChannel : null;
            SetMetric(0, "ui.dash.channel", goCh?.ToString() ?? "—", goCh is int c && ChannelInfo.IsDfs(c) ? Tone.Warn : goCh != null ? Tone.Normal : Tone.Dim);
            SetMetric(1, "ui.dash.width", on && st.Channel?.GoWidthMHz is int w ? Loc.T("ui.dash.mhz", w) : "—", Tone.Dim);
            SetMetric(2, "ui.dash.phy", "—", Tone.Dim);
            SetMetric(3, "ui.dash.streams", "—", Tone.Dim);
            FullText = Loc.T("ui.dash.fullUnknown");
            FullTone = Tone.Dim;
            beaconNote = st.BeaconRunning ? Loc.T("ui.dash.beaconScanning")
                : st.BeaconNote ?? Loc.T(on ? "ui.dash.beaconAuto" : "ui.dash.hotspotOff");
        }

        HotspotRows.Apply(new KvSpec[]
        {
            new("ssid", Loc.T("ui.dash.ssid"), hs?.Ssid),
            new("band", Loc.T("ui.dash.bandAuth"), hs == null ? null : $"{hs.BandText} / {hs.AuthText}"),
            new("clients", Loc.T("ui.dash.clients"), hs == null ? null : $"{hs.ClientCount} / {hs.MaxClients}"),
            new("mac", Loc.T("ui.dash.goMac"), go?.Mac ?? Loc.T(on ? "ui.dash.goSearching" : "ui.dash.hotspotOff")),
            new("ip", Loc.T("ui.dash.ip"),
                go?.Ip != null ? $"{go.Ip}/{(go.Mask != null ? NetworkInfo.PrefixLength(go.Mask) : 24)}" : go != null ? Loc.T("ui.dash.noIp") : null,
                go != null && !go.HasUsableIp ? Tone.Warn : Tone.Normal),
            new("ondongle", Loc.T("ui.dash.onDongle"),
                st.GoOnDongle switch { true => Loc.T("ui.dash.yes"), false => Loc.T("ui.dash.noOther"), _ => null },
                st.GoOnDongle switch { true => Tone.Good, false => Tone.Bad, _ => Tone.Normal }),
            new("note", Loc.T("ui.dash.note"), beaconNote, Tone.Dim, beaconNote),
        });
        OnPropertyChanged(nameof(DevicesEmptyHint));
    }

    private void SetMetric(int index, string labelKey, string value, Tone tone)
    {
        var m = Metrics[index];
        m.Label = Loc.T(labelKey);
        m.Value = value;
        m.Tone = tone;
    }

    private void ApplyNetwork(AppStatus st)
    {
        var hs = st.Hotspot;
        bool on = hs?.State == HotspotState.On;
        var dhcpTone = st.Dhcp.Provider switch
        {
            DhcpProvider.Ours or DhcpProvider.Ics => Tone.Good,
            DhcpProvider.Error => Tone.Bad,
            DhcpProvider.Waiting => Tone.Warn,
            _ => Tone.Normal,
        };
        NetRows.Apply(new KvSpec[]
        {
            new("dhcp", Loc.T("ui.net.dhcp"), st.Dhcp.Text, dhcpTone, st.Dhcp.Text),
            new("dhcpDetail", Loc.T("ui.net.dhcpDetail"), st.Dhcp.Detail, Tone.Normal, st.Dhcp.Detail),
            new("nat", Loc.T("ui.net.nat"), st.Nat.Text, st.Nat.Ok ? Tone.Good : on ? Tone.Warn : Tone.Normal, st.Nat.Text),
            new("internet", Loc.T("ui.net.internet"), st.InternetAllowed ? InternetStrings.Allowed : InternetStrings.LanOnly,
                st.InternetAllowed ? Tone.Normal : Tone.Accent, st.InternetAllowed ? InternetStrings.CheckBox : InternetStrings.Hint),
            new("uplink", Loc.T("ui.net.uplink"), st.UplinkName ?? hs?.UplinkName),
            new("admin", Loc.T("ui.net.admin"), Loc.T(st.IsAdmin ? "ui.net.adminYes" : "ui.net.adminNo"), st.IsAdmin ? Tone.Good : Tone.Warn),
        });
        if (!_internetBusy && InternetAllowed != st.InternetAllowed) Quietly(() => InternetAllowed = st.InternetAllowed);
    }

    // ================================================================== channel

    private void ApplyChannel(AppStatus st)
    {
        var r = st.Channel;
        bool on = st.Hotspot?.State == HotspotState.On;
        string go;
        var goTone = Tone.Normal;
        if (!on) go = ChannelStrings.GoOff;
        else if (r?.GoChannel is int c)
        {
            go = r.GoText;
            if (ChannelInfo.IsDfs(c)) goTone = Tone.Warn;
        }
        else if (st.Beacon != null)
        {
            var bi = st.Beacon.Info;
            go = Loc.T("ui.ch.goFromBeacon", bi.Channel, bi.WidthMHz, ChannelInfo.BlockOf(bi.Channel)?.Name ?? Loc.T("ui.ch.not80"));
        }
        else go = ChannelStrings.GoUnknown;

        string neighbors;
        var nTone = Tone.Normal;
        string? nTip = null;
        if (_scanning) neighbors = ChannelStrings.Scanning;
        else if (r == null) neighbors = ChannelStrings.NoScan;
        else
        {
            var all = r.NeighborLines(30).ToList();
            neighbors = r.Error ?? (all.Count == 0
                ? ChannelStrings.NoNeighbors
                : string.Join("\n", r.Neighbors.Take(4).Zip(r.NeighborLines(4), GlueNeighborTail)) +
                  (r.Neighbors.Count > 4 ? "\n" + Loc.T("ui.ch.more", r.Neighbors.Count) : ""));
            if (r.Error != null) nTone = Tone.Warn;
            nTip = string.Join("\n", all.Prepend(Loc.T("ui.ch.scannedBy", r.Time, r.ScannerName ?? "?")));
        }

        ChannelRows.Apply(new KvSpec[]
        {
            new("go", ChannelStrings.CurrentLabel, go, goTone),
            new("neighbors", ChannelStrings.NeighborsLabel, neighbors, nTone, nTip),
            new("blocks", ChannelStrings.BlocksLabel, r?.OccupancyLine.Replace(" · ", "\n"), Tone.Normal, r?.OccupancyLine),
        });

        var pin = st.Pin;
        PinText = pin?.Text ?? ChannelStrings.PinOff;
        PinTone = pin?.Phase switch
        {
            PinPhase.Verified => Tone.Good,
            PinPhase.Pinned or PinPhase.Unverified => Tone.Accent,
            PinPhase.Suppressed => Tone.Warn,
            _ => Tone.Dim,
        };
        OnPropertyChanged(nameof(ShowPinSection));
        RebuildPinItems(r);
    }

    private void RebuildPinItems(ChannelReport? r)
    {
        if (_pinDropOpen) return;
        var cur = (_s.Channel.PinSsid ?? "").Trim();
        var cands = r?.Candidates ?? (IReadOnlyList<PinCandidate>)Array.Empty<PinCandidate>();
        var key = Loc.Current + "\u0002" + cur + "\u0001" + string.Join("\u0001", cands.Select(c => c.Display));
        if (key == _pinItemsKey) return;
        _pinItemsKey = key;
        Quietly(() =>
        {
            PinItems.Clear();
            PinItems.Add(new PinItemVm(ChannelStrings.PinAuto, "", null));
            foreach (var c in cands) PinItems.Add(new PinItemVm(c.Display, c.Ssid, c));
            if (cur.Length > 0 && !cands.Any(c => c.Ssid == cur)) PinItems.Add(new PinItemVm(cur, cur, null));
            SelectedPin = PinItems.FirstOrDefault(p => p.Ssid == cur) ?? PinItems[0];
        });
    }

    // ================================================================== devices

    private void UpdateClients(IReadOnlyList<ClientRow> rows)
    {
        int online = rows.Count(r => r.Online);
        DevicesSummary = rows.Count == 0
            ? Loc.T("ui.devices.none")
            : Loc.T("ui.devices.online", online) + (rows.Count > online ? Loc.T("ui.devices.recent", rows.Count - online) : "");

        var list = rows.Select(r =>
        {
            bool apipa = r.Ip != null && r.Ip.StartsWith("169.254.", StringComparison.Ordinal);
            string note = r.Ip == null ? (r.Online ? Loc.T("ui.devices.noIpYet") : "") : apipa ? Loc.T("ui.devices.apipa") : "";
            if (!string.IsNullOrEmpty(r.Vendor)) note = string.IsNullOrEmpty(note) ? r.Vendor! : note + Loc.T("common.listSep") + r.Vendor;
            return new DeviceRowVm
            {
                Name = r.HostName ?? Loc.T("ui.devices.unknown"),
                Mac = r.Mac,
                Ip = r.Ip ?? "—",
                State = Loc.T(r.Online ? "ui.devices.stateOnline" : "ui.devices.stateOffline"),
                LastSeen = r.LastSeen?.ToString("MM-dd HH:mm:ss") ?? "",
                Note = note,
                Online = r.Online,
                Tone = !r.Online ? Tone.Dim : r.Ip == null || apipa ? Tone.Warn : Tone.Normal,
            };
        }).ToList();

        var sig = string.Join("\n", list.Select(x => x.CopyText + "|" + x.Tone + "|" + x.Online));
        if (sig == _devicesSig) return;
        _devicesSig = sig;
        var selMac = SelectedDevice?.Mac;
        Devices.Clear();
        foreach (var x in list) Devices.Add(x);
        SelectedDevice = selMac == null ? null : Devices.FirstOrDefault(x => x.Mac == selMac);
        OnPropertyChanged(nameof(HasDevices));
    }

    // ================================================================== status bar

    private void UpdateStatusBar(AppStatus st)
    {
        var drv = st.Driver;
        Seg(0, "ui.sb.driver", drv.Kind switch
        {
            DriverStateKind.DriverOk => (Loc.T("ui.sb.ok"), Tone.Good),
            DriverStateKind.DriverOtherVersion => (Loc.T("ui.sb.otherVersion"), Tone.Warn),
            DriverStateKind.PresentNoDriver => (Loc.T("ui.sb.missing"), Tone.Bad),
            _ => (Loc.T("ui.sb.noDongle"), Tone.Warn),
        }, drv.ShortText);

        Seg(1, "ui.sb.dhcp", st.Dhcp.Provider switch
        {
            DhcpProvider.Ours => (Loc.T("ui.sb.dhcpOurs"), Tone.Good),
            DhcpProvider.Ics => (Loc.T("ui.sb.dhcpIcs"), Tone.Good),
            DhcpProvider.Waiting => (Loc.T("ui.sb.waiting"), Tone.Warn),
            DhcpProvider.Error => (Loc.T("ui.sb.error"), Tone.Bad),
            _ => (Loc.T("ui.sb.off"), Tone.Dim),
        }, st.Dhcp.Text);

        bool on = st.Hotspot?.State == HotspotState.On;
        Seg(2, "ui.sb.nat", st.Nat.Provider == NatProvider.LanOnly
            ? (Loc.T("ui.sb.lanOnly"), Tone.Accent)
            : st.Nat.Ok ? (Loc.T("ui.sb.ok"), Tone.Good) : (Loc.T("ui.sb.off"), on ? Tone.Warn : Tone.Dim), st.Nat.Text);

        Seg(3, "ui.sb.internet", st.InternetAllowed ? (Loc.T("ui.sb.allowed"), Tone.Good) : (Loc.T("ui.sb.lanOnly"), Tone.Accent),
            st.InternetAllowed ? InternetStrings.CheckBox : InternetStrings.Hint);

        int n = st.Clients.Count(c => c.Online);
        Seg(4, null, (Loc.T("ui.sb.connected", n), n > 0 ? Tone.Good : Tone.Dim), DevicesSummary);

        int? ch = on ? st.Channel?.GoChannel ?? st.Beacon?.Info.Channel : null;
        var chTone = ch is not int cc ? Tone.Dim
            : ChannelInfo.IsDfs(cc) || cc <= 14 ? Tone.Warn
            : Tone.Good;
        string? chTip = on ? ChannelRows.FirstOrDefault()?.Value : ChannelStrings.GoOff;
        Seg(5, null, (Loc.T("ui.sb.channel", ch?.ToString() ?? "—"), chTone), chTip);
    }

    private void Seg(int index, string? labelKey, (string Value, Tone Tone) v, string? tip)
    {
        var s = StatusSegs[index];
        s.Label = labelKey == null ? "" : Loc.T(labelKey);
        s.Value = v.Value;
        s.Tone = v.Tone;
        s.Tip = tip;
    }

    // ================================================================== start page

    /// <summary>The first-run guide is shown until set-up is done, while the dongle has no driver, or on request.</summary>
    private void UpdateStartPage(AppStatus? st)
    {
        var drv = st?.Driver ?? new DriverState(DriverStateKind.DongleNotPresent);
        int online = st?.Clients.Count(c => c.Online) ?? 0;
        bool running = st?.State == OverallState.Running;
        bool step1 = drv.IsInstalled;
        bool step2 = AppSettings.Validate(_s.Ssid, _s.Password) == null;
        bool step3 = running && online > 0;

        if (step1 && step2 && step3 && !_s.Ui.SetupDone && !_mock)
        {
            _s.Ui.SetupDone = true;
            Save();
        }

        bool driverMissing = drv.Kind == DriverStateKind.PresentNoDriver;
        ShowStart = _forceStart || (!_startDismissed && (!_s.Ui.SetupDone || driverMissing));

        int current = !step1 ? 1 : !step2 ? 2 : !step3 ? 3 : 0;
        bool[] done = { step1, step2, step3 };
        for (int i = 0; i < 3; i++)
            Steps[i].State = done[i] ? StepState.Done : current == i + 1 ? StepState.Current : StepState.Todo;

        // driver tile
        DriverTile.Status = drv.KindText;
        DriverTile.StatusTone = drv.Kind switch
        {
            DriverStateKind.DriverOk => Tone.Good,
            DriverStateKind.DriverOtherVersion => Tone.Warn,
            DriverStateKind.PresentNoDriver => Tone.Bad,
            _ => Tone.Warn,
        };
        DriverTile.Extra = drv.DonglePresent ? null : Loc.T("ui.start.plugIn");
        DriverTile.ShowButton = !drv.IsInstalled;
        DriverTile.IsCurrent = current == 1;

        // settings tile
        SettingsTile.Status = _s.Ssid;
        SettingsTile.Extra = null;
        SettingsTile.Secret = _s.Password; // shown masked; the tile has an eye button to reveal it
        SettingsTile.StatusTone = step2 ? Tone.Good : Tone.Warn;
        SettingsTile.ShowButton = true;
        SettingsTile.IsCurrent = current == 2;

        // connect tile
        if (running)
        {
            ConnectTile.Status = online > 0 ? Loc.T("ui.start.connectRunning", online) : Loc.T("ui.start.waitingDevices");
            ConnectTile.StatusTone = online > 0 ? Tone.Good : Tone.Accent;
        }
        else
        {
            ConnectTile.Status = StateText.Length > 0 ? StateText : Loc.T("ui.state.stopped");
            ConnectTile.StatusTone = StateTone;
        }
        ConnectTile.Extra = Loc.T("ui.devices.emptyHint", _s.Ssid);
        ConnectTile.ShowButton = st == null || !(st.DesiredOn || running);
        ConnectTile.IsCurrent = current == 3;
    }

    /// <summary>
    /// "Office-Guest  channel 100/80  -80 dBm  DFS": in a narrow column the line may break after the network name,
    /// but the tail (channel / signal / DFS) stays together, so "DFS" never ends up alone on a line. Spaces in the tail
    /// become no-break spaces and a word joiner sits between its other characters (Japanese / Chinese text could
    /// otherwise break between any two characters, e.g. inside the word for "channel").
    /// </summary>
    private static string GlueNeighborTail(NeighborBss n, string line)
    {
        var name = n.DisplayName;
        if (!line.StartsWith(name, StringComparison.Ordinal)) return line;
        var rest = line[name.Length..];
        var tail = rest.TrimStart();
        var sb = new System.Text.StringBuilder(line.Length + tail.Length);
        sb.Append(name).Append(rest, 0, rest.Length - tail.Length);
        for (int i = 0; i < tail.Length; i++)
        {
            char c = tail[i];
            sb.Append(c == ' ' ? DisplayText.NoBreakSpace : c);
            if (c != ' ' && i + 1 < tail.Length && tail[i + 1] != ' ') sb.Append(DisplayText.WordJoiner);
        }
        return sb.ToString();
    }

    /// <summary>Texts that only change with the language (or never).</summary>
    private void RebuildStaticRows()
    {
        var d = _s.Dhcp;
        PoolRows.Apply(new KvSpec[]
        {
            new("mode", Loc.T("ui.net.dhcpMode"), d.Mode),
            new("pool", Loc.T("ui.net.pool"), $"{d.PoolStart} – {d.PoolEnd}"),
            new("lease", Loc.T("ui.net.lease"), Loc.T("ui.net.leaseHours", d.LeaseHours)),
            new("dns", Loc.T("ui.net.dns"), string.Join(Loc.T("common.enumSep"), d.FallbackDns)),
        });

        Steps[0].Title = Loc.T("ui.start.step1");
        Steps[1].Title = Loc.T("ui.start.step2");
        Steps[2].Title = Loc.T("ui.start.step3");

        DriverTile.Title = Loc.T("ui.start.driverTitle");
        DriverTile.Body = Loc.T("ui.start.driverBody", DriverStrings.BundledVersion);
        DriverTile.ButtonText = DriverStrings.ButtonInstall;
        DriverTile.ButtonIcon = "I.download";
        DriverTile.Command = InstallDriverCommand;

        SettingsTile.Title = Loc.T("ui.start.settingsTitle");
        SettingsTile.Body = Loc.T("ui.start.settingsBody");
        SettingsTile.ButtonText = Loc.T("ui.start.settingsButton");
        SettingsTile.ButtonIcon = "I.sliders-horizontal";
        SettingsTile.Command = EditSettingsCommand;

        ConnectTile.Title = Loc.T("ui.start.connectTitle");
        ConnectTile.Body = Loc.T("ui.start.connectBody");
        ConnectTile.ButtonText = Loc.T("ui.header.start");
        ConnectTile.ButtonIcon = "I.play";
        ConnectTile.Command = StartCommand;

        if (_last == null)
        {
            UpdateStartPage(null);
            foreach (var (i, key) in new[] { (0, "ui.dash.channel"), (1, "ui.dash.width"), (2, "ui.dash.phy"), (3, "ui.dash.streams") })
                SetMetric(i, key, Metrics[i].Value, Metrics[i].Tone);
        }
    }

    // ================================================================== log

    private static Tone ToneOf(LogLevel l) => l switch
    {
        LogLevel.Debug => Tone.Dim,
        LogLevel.Ok => Tone.Good,
        LogLevel.Warn => Tone.Warn,
        LogLevel.Error => Tone.Bad,
        _ => Tone.Normal,
    };

    private bool ShowEntry(LogEntry e) => e.Time >= _logClearedAt && (e.Level != LogLevel.Debug || EffectiveDebug);

    private static LogLineVm Line(LogEntry e) => new(e.Time, $"{e.Time:HH:mm:ss} [{e.LevelText}] {e.Message}", ToneOf(e.Level));

    private void RenderLog(List<LogEntry>? entries)
    {
        if (_mock) return;
        entries ??= LogBuffer.Snapshot();
        LogLines = new ObservableCollection<LogLineVm>(entries.Where(ShowEntry).TakeLast(MaxLog).Select(Line));
        OnPropertyChanged(nameof(HasLog));
        LogReset?.Invoke();
    }

    private void FlushLog()
    {
        var items = LogBuffer.DrainPending();
        if (items.Count == 0) return;
        var add = items.Where(ShowEntry).ToList();
        if (add.Count == 0) return;
        if (add.Count > 300) { RenderLog(null); return; }
        foreach (var e in add) _logLines.Add(Line(e));
        while (_logLines.Count > MaxLog) _logLines.RemoveAt(0);
        OnPropertyChanged(nameof(HasLog));
    }

    /// <summary>Screenshots: fixed log lines.</summary>
    internal void SetMockLog(IEnumerable<LogEntry> entries)
    {
        LogLines = new ObservableCollection<LogLineVm>(entries.Where(e => e.Level != LogLevel.Debug || EffectiveDebug).Select(Line));
        OnPropertyChanged(nameof(HasLog));
    }

    /// <summary>Layout check: appends one line the way <see cref="FlushLog"/> does for live entries.</summary>
    internal void AppendMockLog(LogEntry entry)
    {
        _logLines.Add(Line(entry));
        OnPropertyChanged(nameof(HasLog));
    }
}

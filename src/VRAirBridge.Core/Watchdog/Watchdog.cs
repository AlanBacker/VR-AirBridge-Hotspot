using System.Management;
using System.Net;
using Microsoft.Win32;
using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Clients;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core;

/// <summary>
/// 看门狗 + 总调度：每隔几秒检查一次热点、D-Link 网卡、热点网卡（GO）、DHCP、NAT 和已连接设备，
/// 热点掉了按 2、4、8…60 秒的退避自动重开；用户点了“停止”就不再自动开。
/// 所有改变系统状态的操作都串行执行（同一时间只有一个操作）。
/// </summary>
public sealed class Watchdog : IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _s;
    private readonly HotspotController _hc = new();
    private readonly DhcpService _dhcp;
    private readonly NatManager _nat;
    private readonly ChannelPin _pin;
    private readonly bool _isAdmin;
    private readonly string _exePath;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _statusLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // 热点状态跟踪
    private int _failures;
    private DateTime _nextRetry = DateTime.MinValue;
    private DateTime? _onSince;
    private HotspotState _lastState = HotspotState.Unknown;
    private string? _lastError;
    private bool _starting;
    private string? _busy;
    private bool _wrongAdapterHandled;
    private DateTime? _goNoIpSince;
    private bool _goIpFallbackDone;
    private bool? _hadDongle;
    private string? _lastDongleProblem;

    // 驱动
    private DriverState _driver = new(DriverStateKind.DongleNotPresent);
    private DriverStateKind? _lastDriverKind;

    // 信标
    private BeaconResult? _beacon;
    private string? _beaconNote;
    private bool _beaconRunning;
    private DateTime? _beaconDueAt;

    // 设备
    private sealed class ClientState
    {
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public bool Online;
        public bool JoinNotified;
        public List<string> HostNames = new();
    }
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _hintNotified = new();

    // ---- join monitor: WLAN AutoConfig events + IP evidence explain why a device could not join
    private readonly JoinTracker _joins = new();
    private readonly HotspotInterfaceFilter _joinFilter = new();
    private ClientJoinMonitor? _joinMonitor;
    private bool _joinMonitorTried;
    private bool _joinWasOn;

    private AppStatus _status = new() { Headline = Loc.T("watchdog.checking") };

    public event Action<AppStatus>? StatusChanged;
    public event Action<Notification>? Notify;
    /// <summary>D-Link 插上了但没有驱动（每次插上只触发一次，启动时也会触发）。界面据此弹出“立即安装 / 稍后”。</summary>
    public event Action<DriverState>? DriverMissing;

    public Watchdog(SettingsStore store, AppSettings settings, string exePath, bool isAdmin)
    {
        _store = store;
        _s = settings;
        _exePath = exePath;
        _isAdmin = isAdmin;
        _dhcp = new DhcpService(() => _s, store.LeasePath);
        _nat = new NatManager(() => _s, SaveSettings);
        _pin = new ChannelPin(() => _s, SaveSettings);
        _pin.Notice += text => Raise(NotifyKind.Warning, ChannelStrings.NotifyTitle, text);
        _hc.Pin = _pin;
        _dhcp.LeaseChanged += OnLeaseChanged;
    }

    /// <summary>用户希望热点开着（点了“启动”或设置了启动时自动开）。</summary>
    public bool DesiredOn { get; private set; }
    public bool IsAdmin => _isAdmin;
    public AppStatus Status { get { lock (_statusLock) return _status; } }
    public AppSettings Settings => _s;
    public HotspotController Controller => _hc;
    public DhcpService Dhcp => _dhcp;
    public BeaconResult? LastBeacon => _beacon;
    public string? LastBeaconNote => _beaconNote;
    public ChannelPin Pin => _pin;
    public string DataDirectory => _store.DataDirectory;

    public void SaveSettings()
    {
        try { _store.Save(_s); }
        catch (Exception ex) { Logger.Error(Loc.T("watchdog.failedSaveConfigJson") + ex.Message); }
    }

    // ================================================================ 初始化

    /// <summary>
    /// 第一次运行：如果 Windows 移动热点里已经是 “AirBridge-” 开头的热点名（之前的脚本设置过），沿用它和它的密码，
    /// 已配对的设备不用重新输入密码；否则生成 “AirBridge-网卡MAC后4位” 和 12 位随机密码。
    /// </summary>
    public void EnsureInitialConfig()
    {
        if (AppSettings.Validate(_s.Ssid, _s.Password) == null) { Logger.RegisterSecret(_s.Password); return; }
        var cur = _hc.TryReadCurrentConfig();
        if (cur is { } c && c.Ssid.StartsWith("AirBridge-", StringComparison.OrdinalIgnoreCase) && AppSettings.Validate(c.Ssid, c.Passphrase) == null)
        {
            _s.Ssid = c.Ssid;
            _s.Password = c.Passphrase;
            Logger.RegisterSecret(_s.Password);
            Logger.Info(Loc.T("watchdog.keepingExistingHotspotNamePassword", c.Ssid));
        }
        else
        {
            var last4 = MacUtil.Last4(DongleInfo.Find()?.Mac);
            _s.Ssid = "AirBridge-" + (last4.Length == 4 ? last4 : "VR");
            _s.Password = AppSettings.GeneratePassword(12);
            Logger.RegisterSecret(_s.Password);
            Logger.Info(Loc.T("watchdog.generatedDefaultHotspotNameRandom", _s.Ssid));
        }
        SaveSettings();
    }

    public void Start(bool hotspotOnLaunch)
    {
        if (_loop != null) return;
        try
        {
            var snap = _hc.GetSnapshot();
            DesiredOn = hotspotOnLaunch || snap.State == HotspotState.On;
            if (snap.State == HotspotState.On && !hotspotOnLaunch) Logger.Info(Loc.T("watchdog.hotspotAlreadyRunningWatchdogTakes"));
        }
        catch { DesiredOn = hotspotOnLaunch; }

        if (_isAdmin)
        {
            Task.Run(() =>
            {
                var chk = FirewallManager.Check(_exePath);
                if (!chk.AllOk) FirewallManager.EnsureRules(_exePath);
            });
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _op.WaitAsync(ct);
                try { await TickAsync(ct); }
                finally { _op.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Error(Loc.T("watchdog.watchdogError") + ex.Message); Logger.Debug(ex.ToString()); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(_s.WatchdogIntervalSeconds, 2, 30)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private static int Backoff(int failures) => (int)Math.Min(60, Math.Pow(2, Math.Max(1, failures)));

    // ================================================================ 每个周期

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dongle = DongleInfo.Find();
        // 告诉热点控制器哪块是 D-Link：它绝不能被当作上行（拔掉时保留上次的，避免误判）
        if (dongle != null) _hc.SetDongle(dongle.Guid, dongle.Mac);
        TrackDongle(dongle);
        TrackDriver(dongle);
        var snap = _hc.GetSnapshot();
        bool on = snap.State == HotspotState.On;

        if (snap.State != _lastState)
        {
            if (_lastState != HotspotState.Unknown || snap.State == HotspotState.On)
                Logger.Write(snap.State == HotspotState.On ? LogLevel.Ok : LogLevel.Info, Loc.T("watchdog.hotspotState") + HotspotController.StateText(snap.State));
            if (_lastState == HotspotState.On && !on && DesiredOn)
            {
                bool shortLived = _onSince != null && now - _onSince < TimeSpan.FromSeconds(60);
                if (shortLived) _failures++;
                _nextRetry = now.AddSeconds(Backoff(_failures));
                Logger.Warn(Loc.T("watchdog.hotspotStoppedUnexpectedlyRestartingAutomatically", Backoff(_failures)));
                Raise(NotifyKind.Warning, Loc.T("watchdog.hotspotStoppedUnexpectedly"), Loc.T("watchdog.restartingAutomaticallyS", Backoff(_failures)));
            }
            // 热点从开变为关：上一次的信标结果已过期，清掉（下次开启会重新核对）
            if (_lastState == HotspotState.On && !on) { _beacon = null; _beaconNote = null; }
            _lastState = snap.State;
        }

        if (on)
        {
            _onSince ??= now;
            if (_failures > 0 && now - _onSince > TimeSpan.FromSeconds(60)) { _failures = 0; Logger.Debug(Loc.T("watchdog.hotspotHasBeenStable60")); }
            _beaconDueAt ??= _beacon == null && !_beaconRunning ? now.AddSeconds(8) : null;
        }
        else
        {
            _onSince = null;
            _wrongAdapterHandled = false;
            _goIpFallbackDone = false;
            _goNoIpSince = null;
            _beaconDueAt = null;
            // 热点被别处关掉、也不打算重开：断开钉住用的 D-Link 连接
            if (!DesiredOn && _pin.Active) _pin.Release(Loc.T("watchdog.hotspotStopped"));
        }

        // ---- 需要时启动热点
        if (DesiredOn && !on && snap.State != HotspotState.InTransition)
        {
            var problem = dongle == null
                ? (_driver.Kind == DriverStateKind.PresentNoDriver
                    ? DriverStrings.StartNeedsDriver
                    : Loc.T("watchdog.dLinkDwaF18Adapter"))
                : dongle.IsDisabled ? Loc.T("watchdog.dLinkAdapterDisabledDevice") : null;
            if (dongle != null && dongle.IsDisabled && _isAdmin)
            {
                Logger.Warn(Loc.T("watchdog.dLinkAdapterDisabledEnabling"));
                if (DongleInfo.TryEnable(dongle)) { await Task.Delay(3000, ct); dongle = DongleInfo.Find(); problem = dongle == null || dongle.IsDisabled ? problem : null; }
            }
            if (problem != null)
            {
                if (_lastDongleProblem != problem) { _lastDongleProblem = problem; Logger.Error(problem); Raise(NotifyKind.Error, Loc.T("watchdog.cannotStartHotspot"), problem); }
                _lastError = problem;
            }
            else if (now >= _nextRetry)
            {
                _lastDongleProblem = null;
                await TryStartAsync(ct);
                snap = _hc.GetSnapshot();
                on = snap.State == HotspotState.On;
                if (on) { _onSince ??= DateTime.UtcNow; _lastState = HotspotState.On; }
            }
        }

        // ---- 热点网卡（GO）
        var go = NetworkInfo.FindGoAdapter(dongle?.Mac);
        if (!on) go = null;
        bool? goOnDongle = go != null && dongle != null ? MacUtil.SameRadio(go.Mac, dongle.Mac) : null;

        // ---- 钉住信道（实验）：热点仍在 D-Link 上、信标信道 == D-Link 连接的信道，否则回退
        if (on && _pin.Active && !_starting)
        {
            var why = _pin.Verify(on, go != null ? goOnDongle : null, _beacon);
            if (why != null)
            {
                await PinFallbackAsync(why, ct);
                snap = _hc.GetSnapshot();
                on = snap.State == HotspotState.On;
                go = on ? NetworkInfo.FindGoAdapter(dongle?.Mac) : null;
                goOnDongle = go != null && dongle != null ? MacUtil.SameRadio(go.Mac, dongle.Mac) : null;
            }
        }

        if (on && go != null && goOnDongle == false && !_wrongAdapterHandled)
        {
            _wrongAdapterHandled = true;
            await HandleWrongAdapterAsync(go, dongle!, snap, ct);
            go = NetworkInfo.FindGoAdapter(dongle?.Mac);
            goOnDongle = go != null && dongle != null ? MacUtil.SameRadio(go.Mac, dongle.Mac) : null;
        }

        if (on && go != null && !go.HasUsableIp)
        {
            _goNoIpSince ??= now;
            if (now - _goNoIpSince > TimeSpan.FromSeconds(12) && _isAdmin && !_goIpFallbackDone)
            {
                _goIpFallbackDone = true;
                AssignGoIpFallback(go);
                go = NetworkInfo.FindGoAdapter(dongle?.Mac);
            }
        }
        else _goNoIpSince = null;

        // ---- DHCP / NAT
        var uplink = NetworkInfo.GetUplink(snap.UplinkAdapterId);
        var dhcpSt = _dhcp.Reconcile(go, uplink, on, _isAdmin);
        var natSt = on && go != null
            ? _nat.Reconcile(go, uplink, dhcpSt.Provider == DhcpProvider.Ics, _isAdmin)
            : new NatStatus(NatProvider.None, Loc.T("watchdog.notRunning"), false);

        // ---- 信标核对
        if (on && go != null && dongle != null && _beaconDueAt != null && now >= _beaconDueAt && !_beaconRunning)
        {
            _beaconDueAt = null;
            _ = RunBeaconAsync(go.Mac, dongle.Guid, CancellationToken.None);
        }

        // ---- 设备列表
        var rows = UpdateClients(snap, go, now);

        // ---- 加入过程诊断（密码 / 握手后断开 / 拿不到 IP / 反复重连）
        try { TrackJoins(snap, go, dhcpSt, on, now); }
        catch (Exception ex) { Logger.Debug("join monitor: " + ex.Message); }

        // ---- 提示
        var hints = BuildHints(snap, go, goOnDongle, dhcpSt, natSt, rows, now);

        Publish(snap, dongle, go, goOnDongle, dhcpSt, natSt, uplink, rows, hints);
    }

    /// <summary>每个周期用已查到的信息判断驱动状态；D-Link 插上但没驱动时，每次插上只提醒一次。</summary>
    private void TrackDriver(DongleInfo? dongle)
    {
        var usb = dongle == null ? DongleInfo.FindUsbWithoutDriver() : null;
        var d = DriverInstaller.FromProbe(dongle, usb);
        _driver = d;
        if (d.Kind != _lastDriverKind)
        {
            if (d.Kind == DriverStateKind.PresentNoDriver)
            {
                Logger.Warn(DriverStrings.InlineWarning);
                Raise(NotifyKind.Warning, DriverStrings.NotifyTitle, DriverStrings.NotifyText);
                try { DriverMissing?.Invoke(d); } catch { }
            }
            else if (_lastDriverKind == DriverStateKind.PresentNoDriver && d.IsInstalled)
                Logger.Ok(Loc.T("watchdog.dLinkDriverReady") + d.ShortText);
            _lastDriverKind = d.Kind;
        }
    }

    // ================================================================ 钉住信道（实验）回退

    private async Task PinFallbackAsync(string why, CancellationToken ct)
    {
        var ssid = _pin.Status.Ssid ?? (_s.Channel.PinSsid ?? "").Trim();
        Logger.Warn(string.Format(ChannelStrings.PinFallbackLog, why));
        _starting = true;
        PublishBusy(Loc.T("watchdog.fallingBackRestartingHotspotWithout"));
        try
        {
            _dhcp.StopServer(Loc.T("watchdog.channelPinFallback"));
            try { await _hc.StopAsync(ct); }   // StopAsync 会断开 D-Link 的连接、恢复跃点数
            catch (Exception ex) when (ex is not OperationCanceledException) { Logger.Warn(Loc.T("watchdog.failedStopHotspotDuringFallback") + ex.Message); _pin.Release(Loc.T("watchdog.fallback")); }
            _pin.Suppress(ssid, ChannelStrings.PinFallback);
            await Task.Delay(2000, ct);
            try
            {
                await _hc.StartAsync(_s, ct);
                Logger.Ok(Loc.T("watchdog.hotspotRestartedWithoutPin"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 绝不让热点就这么关着：交给看门狗按退避重试
                DesiredOn = true;
                _lastError = ex is HotspotException ? ex.Message : Loc.T("watchdog.hotspotFailedStart") + ex.Message;
                _nextRetry = DateTime.UtcNow;
                Logger.Error(Loc.T("watchdog.failedRestartHotspotAfterFallback", _lastError));
            }
            _beacon = null;
            _beaconDueAt = DateTime.UtcNow.AddSeconds(8);
            _wrongAdapterHandled = false;
        }
        finally
        {
            _starting = false;
            _busy = null;
        }
    }

    private void TrackDongle(DongleInfo? d)
    {
        bool has = d != null;
        if (_hadDongle == true && !has)
        {
            Logger.Error(Loc.T("watchdog.dLinkAdapterWasUnplugged"));
            Raise(NotifyKind.Error, Loc.T("watchdog.dLinkAdapterDroppedOut"), Loc.T("watchdog.dLinkDwaF18Not"));
        }
        else if (_hadDongle == false && has)
        {
            Logger.Ok(Loc.T("watchdog.dLinkAdapterDetected"));
            _nextRetry = DateTime.MinValue;
            _failures = 0;
        }
        _hadDongle = has;
    }

    private async Task TryStartAsync(CancellationToken ct)
    {
        _starting = true;
        PublishBusy(Loc.T("watchdog.startingHotspot"));
        try
        {
            await _hc.StartAsync(_s, ct);
            _lastError = null;
            Logger.Ok(Loc.T("watchdog.hotspotStarted"));
            _beacon = null;
            _beaconDueAt = DateTime.UtcNow.AddSeconds(8);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failures++;
            int delay = Backoff(_failures);
            _nextRetry = DateTime.UtcNow.AddSeconds(delay);
            _lastError = ex is HotspotException ? ex.Message : Loc.T("watchdog.hotspotFailedStart") + ex.Message;
            Logger.Error(Loc.T("watchdog.attemptRetryingS", _lastError, _failures, delay));
            if (_failures == 1 || _failures % 5 == 0) Raise(NotifyKind.Error, Loc.T("watchdog.hotspotFailedStart2"), Loc.T("watchdog.retryingAutomaticallyS", _lastError, delay));
        }
        finally
        {
            _starting = false;
            _busy = null;
        }
    }

    // ================================================================ 热点开错网卡

    private sealed record WlanAdapter(string Name, string Guid, string Mac, int State, int MediaConnectState);

    private static List<WlanAdapter> WirelessAdapters()
    {
        var list = new List<WlanAdapter>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"), new ObjectQuery(
                "SELECT Name, InterfaceGuid, PermanentAddress, State, InterfaceAdminStatus, MediaConnectState, NdisPhysicalMedium, HardwareInterface FROM MSFT_NetAdapter"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                {
                    if (Convert.ToInt32(o["NdisPhysicalMedium"] ?? 0) != 9) continue;
                    if (o["HardwareInterface"] is bool hw && !hw) continue;
                    list.Add(new WlanAdapter(o["Name"] as string ?? "", o["InterfaceGuid"] as string ?? "", MacUtil.Format(o["PermanentAddress"] as string),
                        DongleInfo.EffectiveState(o), Convert.ToInt32(o["MediaConnectState"] ?? 0)));
                }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 热点被 Windows 开在了别的无线网卡上：临时禁用“既不是 D-Link、也不是上网用、也没有连着 Wi-Fi”的无线网卡，
    /// 重开热点后再启用它们。绝不动正在联网的网卡（不会断开电脑自己的 Wi-Fi）。
    /// </summary>
    private async Task HandleWrongAdapterAsync(GoAdapterInfo go, DongleInfo dongle, HotspotSnapshot snap, CancellationToken ct)
    {
        Logger.Warn(Loc.T("watchdog.hotspotStartedMacNotD", go.Description, go.Mac, dongle.Mac));
        Raise(NotifyKind.Warning, Loc.T("watchdog.hotspotNotDLink"), Loc.T("watchdog.windowsStartedHotspotAnotherWi"));
        var upGuid = (snap.UplinkAdapterId ?? "").Trim('{', '}');
        var others = WirelessAdapters().Where(a =>
            !MacUtil.SameRadio(a.Mac, dongle.Mac) &&
            !string.Equals(a.Guid.Trim('{', '}'), upGuid, StringComparison.OrdinalIgnoreCase) &&
            a.State != 3 && a.MediaConnectState != 1).ToList();
        if (others.Count == 0)
        {
            Logger.Warn(Loc.T("watchdog.noOtherWiFiAdapter"));
            try
            {
                await _hc.StopAsync(ct);
                await Task.Delay(3000, ct);
                await _hc.StartAsync(_s, ct);
                await Task.Delay(3000, ct);
            }
            catch (Exception ex) { Logger.Warn(Loc.T("watchdog.failedRestartHotspot") + ex.Message); }
        }
        else if (!_isAdmin)
        {
            Logger.Warn(Loc.T("watchdog.administratorRightsAreRequiredTemporarily"));
            return;
        }
        else
        {
            var names = others.Select(o => o.Name).ToList();
            Logger.Warn(Loc.T("watchdog.temporarilyDisablingOtherOfflineWi") + string.Join(", ", names));
            try
            {
                await _hc.StopAsync(ct);
                foreach (var n in names) IcsRepair.SetAdapterEnabled(n, false);
                await Task.Delay(3000, ct);
                await _hc.StartAsync(_s, ct);
                await Task.Delay(3000, ct);
            }
            catch (Exception ex) { Logger.Warn(Loc.T("watchdog.errorWhileMovingHotspot") + ex.Message); }
            finally
            {
                foreach (var n in names) IcsRepair.SetAdapterEnabled(n, true);
                Logger.Info(Loc.T("watchdog.otherWiFiAdaptersRestored"));
            }
        }
        var now = NetworkInfo.FindGoAdapter(dongle.Mac);
        if (now != null && MacUtil.SameRadio(now.Mac, dongle.Mac)) Logger.Ok(Loc.T("watchdog.fixedHotspotNowDLink"));
        else
        {
            Logger.Error(Loc.T("watchdog.hotspotStillNotDLink"));
            Raise(NotifyKind.Error, Loc.T("watchdog.hotspotNotDLink"), Loc.T("watchdog.automaticCorrectionFailedRePlug"));
        }
    }

    /// <summary>热点网卡一直没拿到 192.168.137.x 时，临时（重启后失效）给它加上 ICS 的网关地址。</summary>
    private void AssignGoIpFallback(GoAdapterInfo go)
    {
        string scope = "192.168.137.1";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters");
            if (k?.GetValue("ScopeAddress") is string sa && IPAddress.TryParse(sa, out _)) scope = sa;
        }
        catch { }
        Logger.Warn(Loc.T("watchdog.hotspotAdapterHasHadNo", go.Name, scope));
        var r = ProcessRunner.PowerShell(
            $"try {{ New-NetIPAddress -InterfaceIndex {go.IfIndex} -IPAddress {ProcessRunner.Q(scope)} -PrefixLength 24 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 30000);
        if (r.Success) Logger.Ok(Loc.T("watchdog.assigned24HotspotAdapter", scope));
        else Logger.Warn(Loc.T("watchdog.failedSetHotspotAdapterIp") + r.Combined.Replace("ERR:", ""));
    }

    // ================================================================ 信标

    private async Task RunBeaconAsync(string goMac, string? dongleGuid, CancellationToken ct)
    {
        if (_beaconRunning) return;
        _beaconRunning = true;
        try
        {
            var (res, reason) = await BeaconChecker.CheckAsync(goMac, dongleGuid, ct);
            _beacon = res;
            _beaconNote = reason;
            if (res != null)
            {
                Logger.Write(res.Info.IsFull ? LogLevel.Ok : LogLevel.Warn,
                    Loc.T("watchdog.beaconCheck", res.ScannerName, res.Summary) + (res.Info.IsFull ? Loc.T("watchdog.fullSpeed") : Loc.T("watchdog.notFullSpeed")));
                if (res.SameChannelOthers.Count > 0)
                    Logger.Info(Loc.T("watchdog.otherWiFiNetworksSame", res.SameChannelOthers.Count, string.Join(", ", res.SameChannelOthers.Take(6))));
            }
            else if (reason != null) Logger.Info(Loc.T("watchdog.beaconCheck2") + reason);
        }
        catch (Exception ex) { _beaconNote = Loc.T("watchdog.beaconCheckFailed") + ex.Message; }
        finally { _beaconRunning = false; }
        Wake();
    }

    public async Task CheckBeaconNowAsync()
    {
        var st = Status;
        if (st.Go == null || st.Hotspot?.State != HotspotState.On)
        {
            Logger.Warn(Loc.T("watchdog.hotspotIsnTRunningSo"));
            return;
        }
        await RunBeaconAsync(st.Go.Mac, st.Dongle?.Guid, CancellationToken.None);
    }

    // ================================================================ 设备

    private void OnLeaseChanged(LeaseEvent e)
    {
        var name = e.Lease.HostName ?? e.Lease.Mac;
        switch (e.Kind)
        {
            case LeaseEventKind.Granted:
                Logger.Ok(Loc.T("watchdog.deviceGotIp", name, e.Lease.Mac, e.Lease.Ip));
                break;
            case LeaseEventKind.Released:
                Logger.Info(Loc.T("watchdog.deviceReleasedIp", name, e.Lease.Ip));
                break;
            case LeaseEventKind.Declined:
                Logger.Warn(Loc.T("watchdog.deviceReportedAddressConflict", name, e.Lease.Ip));
                break;
        }
        Wake();
    }

    private List<ClientRow> UpdateClients(HotspotSnapshot snap, GoAdapterInfo? go, DateTime now)
    {
        var leases = _dhcp.Leases.All().ToDictionary(l => MacUtil.Format(l.Mac), StringComparer.OrdinalIgnoreCase);
        var arp = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        if (go != null)
        {
            foreach (var a in IpHelper.GetArpTable())
            {
                if (a.InterfaceIndex != go.IfIndex || (a.Type != 3 && a.Type != 4)) continue;
                var b = a.Address.GetAddressBytes();
                if (b[0] >= 224 || b[3] == 255) continue;
                arp[MacUtil.Format(a.Mac)] = a.Address;
            }
        }

        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in snap.Clients)
        {
            var mac = MacUtil.Format(c.Mac);
            if (string.IsNullOrEmpty(mac)) continue;
            online.Add(mac);
            if (!_clients.TryGetValue(mac, out var st)) _clients[mac] = st = new ClientState { FirstSeen = now };
            if (!st.Online) { st.Online = true; st.FirstSeen = now; st.JoinNotified = false; }
            st.LastSeen = now;
            st.HostNames = c.HostNames.Where(h => !IPAddress.TryParse(h, out _)).ToList();
        }

        string? IpOf(string mac)
        {
            if (leases.TryGetValue(mac, out var l) && l.IsActive(now)) return l.Ip;
            return arp.TryGetValue(mac, out var ip) ? ip.ToString() : null;
        }
        string NameOf(string mac)
        {
            if (leases.TryGetValue(mac, out var l) && !string.IsNullOrEmpty(l.HostName)) return l.HostName!;
            if (_clients.TryGetValue(mac, out var st) && st.HostNames.Count > 0) return st.HostNames[0];
            return mac;
        }

        foreach (var (mac, st) in _clients.ToList())
        {
            if (st.Online && !online.Contains(mac))
            {
                st.Online = false;
                if (st.JoinNotified)
                {
                    Logger.Info(Loc.T("watchdog.deviceDisconnected", NameOf(mac), mac));
                    Raise(NotifyKind.Info, Loc.T("watchdog.deviceDisconnected2"), NameOf(mac));
                }
            }
            else if (st.Online && !st.JoinNotified)
            {
                var ip = IpOf(mac);
                if (ip != null || now - st.FirstSeen > TimeSpan.FromSeconds(8))
                {
                    st.JoinNotified = true;
                    var text = ip != null ? $"{NameOf(mac)}  IP {ip}" : Loc.T("watchdog.noIpYet", NameOf(mac));
                    Logger.Write(ip != null && !ip.StartsWith("169.254.") ? LogLevel.Ok : LogLevel.Warn, Loc.T("watchdog.deviceConnected", text, mac));
                    Raise(NotifyKind.Info, Loc.T("watchdog.deviceConnected2"), text);
                }
            }
            if (!st.Online && now - st.LastSeen > TimeSpan.FromHours(2)) _clients.Remove(mac);
        }

        var rows = new List<ClientRow>();
        foreach (var mac in online)
        {
            leases.TryGetValue(mac, out var l);
            rows.Add(new ClientRow(mac, IpOf(mac), NameOf(mac) == mac ? null : NameOf(mac), DateTime.Now, true, l?.VendorClass));
        }
        foreach (var l in leases.Values.Where(l => !online.Contains(MacUtil.Format(l.Mac)) && now - l.LastSeen < TimeSpan.FromHours(2)))
        {
            var mac = MacUtil.Format(l.Mac);
            rows.Add(new ClientRow(mac, l.IsActive(now) ? l.Ip : null, l.HostName, l.LastSeen.ToLocalTime(), false, l.VendorClass));
        }
        return rows;
    }

    private List<string> BuildHints(HotspotSnapshot snap, GoAdapterInfo? go, bool? goOnDongle, DhcpStatus dhcp, NatStatus nat, List<ClientRow> rows, DateTime now)
    {
        var hints = new List<string>();
        var av = Loc.T("watchdog.ifYouUseSecuritySoftware");
        if (!_isAdmin)
            hints.Add(Loc.T("watchdog.notRunningAsAdministratorBuilt"));
        if (goOnDongle == false)
            hints.Add(Loc.T("watchdog.hotspotRunningNotDLink", go?.Description));
        if (snap.CapabilityProblem != null && snap.State != HotspotState.On)
            hints.Add(Loc.T("watchdog.systemReports") + snap.CapabilityProblem);

        if (dhcp.Provider == DhcpProvider.Ours)
        {
            foreach (var mac in _dhcp.StuckClients())
            {
                hints.Add(Loc.T("watchdog.deviceKeepsRequestingIpApp", mac, av));
                NotifyHintOnce("stuck:" + mac, NotifyKind.Warning, Loc.T("watchdog.deviceCanTGetIp"), Loc.T("watchdog.ipAddressesSentByApp"));
            }
            var traces = _dhcp.Traces();
            foreach (var r in rows.Where(r => r.Online && r.Ip == null))
            {
                if (_clients.TryGetValue(r.Mac, out var st) && now - st.FirstSeen > TimeSpan.FromSeconds(25) && !traces.ContainsKey(r.Mac.Replace(":", "-")))
                {
                    hints.Add(Loc.T("watchdog.deviceConnectedHotspotButApp", r.HostName ?? r.Mac, av));
                    NotifyHintOnce("noreq:" + r.Mac, NotifyKind.Warning, Loc.T("watchdog.noIpRequestsFromDevices"), Loc.T("watchdog.windowsFirewallOrKasperskyBlocking"));
                }
            }
        }
        foreach (var r in rows.Where(r => r.Online && r.Ip != null && r.Ip.StartsWith("169.254.")))
            hints.Add(Loc.T("watchdog.deviceUsingSelfAssigned169", r.HostName ?? r.Mac));
        if (dhcp.Provider == DhcpProvider.Error && _isAdmin)
            hints.Add(Loc.T("watchdog.builtDhcpIsnTRunning") + dhcp.Text);
        if (_driver.Kind == DriverStateKind.PresentNoDriver)
            hints.Add(DriverStrings.InlineWarning);
        if (snap.State == HotspotState.On && go != null && go.HasUsableIp && !nat.Ok && nat.Provider is not (NatProvider.Disabled or NatProvider.LanOnly))
            hints.Add(Loc.T("watchdog.devicesCanConnectHotspotBut") + nat.Text);
        if (!_s.Internet.Allow && snap.State == HotspotState.On && dhcp.Provider == DhcpProvider.Ics)
            hints.Add(InternetStrings.IcsHint);
        if (_beacon != null && !_beacon.Info.IsFull)
            hints.Add(Loc.T("watchdog.beaconShowsHotspotIsnT", _beacon.Summary));
        return hints;
    }

    /// <summary>The devices the join monitor knows (joining, connected, failed), most recent first.</summary>
    public IReadOnlyList<ClientJoinRecord> JoinRecords => _joins.Snapshot();

    /// <summary>The join monitor's recent findings (oldest first), for the diagnostics report.</summary>
    public IReadOnlyList<JoinFinding> JoinHistory => _joins.History();

    /// <summary>Why the WLAN event monitor is not running (null = running or not started yet).</summary>
    public string? JoinMonitorError => _joinMonitor?.Error;

    private void TrackJoins(HotspotSnapshot snap, GoAdapterInfo? go, DhcpStatus dhcp, bool on, DateTime now)
    {
        if (on && go != null)
        {
            _joinFilter.Add(go.Id, go.Mac);
            if (!_joinMonitorTried)
            {
                _joinMonitorTried = true;
                var m = new ClientJoinMonitor(_joins, _joinFilter);
                // backlog: what happened since the hotspot came up (at most 15 minutes), so joins made while the app started are explained too
                var since = new[] { now - TimeSpan.FromMinutes(15), (_onSince ?? now) - TimeSpan.FromMinutes(1) }.Max();
                if (m.Start(since)) { _joinMonitor = m; Logger.Debug(Loc.T("join.monitorOn")); }
                else { Logger.Debug(Loc.T("join.monitorUnavailable", m.Error ?? "?")); m.Dispose(); }
            }
        }
        if (_joinWasOn && !on) _joins.HotspotStopped(now);
        _joinWasOn = on;

        var ctx = new JoinContext
        {
            Dhcp = dhcp.Provider switch { DhcpProvider.Ours => JoinDhcp.Ours, DhcpProvider.Ics => JoinDhcp.Ics, _ => JoinDhcp.Other },
            Wpa3 = _s.Auth.Contains("WPA3", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var l in _dhcp.Leases.All()) ctx.AddLease(l.Mac, l.Ip, l.HostName, l.LastSeen, l.IsActive(now));
        foreach (var (mac, t) in _dhcp.Traces()) ctx.AddDhcpPacket(mac, t.LastPacket);
        if (go != null) foreach (var n in IpHelper.GetNeighbors(go.IfIndex)) ctx.AddNeighbor(n);
        if (on)
        {
            ctx.Present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in snap.Clients) ctx.AddPresent(c.Mac, c.HostNames.FirstOrDefault(h => !IPAddress.TryParse(h, out _)));
        }
        ReportJoins(_joins.Tick(now, ctx));
    }

    /// <summary>Logs every finding; notifies only for live failures the other hints do not already cover.</summary>
    private void ReportJoins(List<JoinFinding> found)
    {
        foreach (var f in found)
        {
            var msg = f.Message;
            if (f.IsSuccess) { Logger.Debug(msg); continue; }
            Logger.Warn(msg);
            if (f.FromBacklog) continue;
            bool toast = f.Failure switch
            {
                JoinFailure.WrongPassword or JoinFailure.DroppedAfterAuth or JoinFailure.ReconnectLoop => true,
                // this app's DHCP: the "no IP requests" hint already notifies
                JoinFailure.NoIp => f.Dhcp != JoinDhcp.Ours,
                _ => false,
            };
            if (toast) Raise(NotifyKind.Warning, JoinText.Title(f), msg);
        }
    }

    private void NotifyHintOnce(string key, NotifyKind kind, string title, string text)
    {
        if (_hintNotified.TryGetValue(key, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(10)) return;
        _hintNotified[key] = DateTime.UtcNow;
        Logger.Warn(title + Loc.T("common.colon") + text);
        Raise(kind, title, text);
    }

    // ================================================================ 状态发布

    private void Publish(HotspotSnapshot snap, DongleInfo? dongle, GoAdapterInfo? go, bool? goOnDongle, DhcpStatus dhcp, NatStatus nat,
        UplinkInfo? uplink, List<ClientRow> rows, List<string> hints)
    {
        bool on = snap.State == HotspotState.On;
        OverallState state;
        string detail;
        int? retryIn = null;
        if (_starting) { state = OverallState.Starting; detail = Loc.T("watchdog.startingHotspot2"); }
        else if (on)
        {
            state = OverallState.Running;
            int n = rows.Count(r => r.Online);
            detail = Loc.T("watchdog.nameDevicesConnected", snap.Ssid, snap.BandText ?? "?", n, InternetStrings.StatusText(_s.Internet.Allow)) + (DesiredOn ? "" : Loc.T("watchdog.notStartedByApp"));
        }
        else if (!DesiredOn) { state = OverallState.Stopped; detail = Loc.T("watchdog.clickStartHotspotTurnIt"); }
        else if (_failures > 0 && dongle != null && !dongle.IsDisabled)
        {
            state = OverallState.Retrying;
            retryIn = Math.Max(0, (int)Math.Ceiling((_nextRetry - DateTime.UtcNow).TotalSeconds));
            detail = Loc.T("watchdog.startAttemptFailedRetryingS", _failures, retryIn, _lastError);
        }
        else if (_lastError != null || snap.State is HotspotState.NoUplink or HotspotState.Error)
        {
            state = OverallState.Error;
            detail = _lastError ?? snap.Error ?? Loc.T("watchdog.hotspotUnavailable");
        }
        else { state = OverallState.Starting; detail = HotspotController.StateText(snap.State); }

        var st = new AppStatus
        {
            State = state,
            Headline = AppStatus.HeadlineFor(state),
            Detail = detail,
            BusyText = _busy,
            DesiredOn = DesiredOn,
            IsAdmin = _isAdmin,
            Hotspot = snap,
            Dongle = dongle,
            Go = go,
            GoOnDongle = goOnDongle,
            Beacon = _beacon,
            BeaconNote = _beaconNote,
            BeaconRunning = _beaconRunning,
            Dhcp = dhcp,
            Nat = nat,
            UplinkName = uplink != null ? Loc.T("common.nameParen", snap.UplinkName, uplink.Name) : snap.UplinkName,
            Clients = rows,
            Joins = _joins.Snapshot(),
            Hints = hints,
            RetryInSeconds = retryIn,
            Driver = _driver,
            Channel = ChannelInfo.Last,
            Pin = _pin.Status,
            InternetAllowed = _s.Internet.Allow,
        };
        lock (_statusLock) _status = st;
        try { StatusChanged?.Invoke(st); } catch { }
    }

    private void PublishBusy(string text)
    {
        _busy = text;
        AppStatus cur;
        lock (_statusLock)
        {
            var o = _status;
            cur = new AppStatus
            {
                State = OverallState.Starting, Headline = o.Headline, Detail = o.Detail, BusyText = text, DesiredOn = DesiredOn, IsAdmin = _isAdmin,
                Hotspot = o.Hotspot, Dongle = o.Dongle, Go = o.Go, GoOnDongle = o.GoOnDongle, Beacon = o.Beacon, BeaconNote = o.BeaconNote,
                BeaconRunning = o.BeaconRunning, Dhcp = o.Dhcp, Nat = o.Nat, UplinkName = o.UplinkName, Clients = o.Clients, Joins = o.Joins, Hints = o.Hints,
                RetryInSeconds = o.RetryInSeconds, Driver = _driver, Channel = ChannelInfo.Last, Pin = _pin.Status, InternetAllowed = _s.Internet.Allow,
            };
            _status = cur;
        }
        try { StatusChanged?.Invoke(cur); } catch { }
    }

    private void Raise(NotifyKind kind, string title, string text)
    {
        try { Notify?.Invoke(new Notification(kind, title, text)); } catch { }
    }

    // ================================================================ 用户操作

    private async Task<T> RunOpAsync<T>(string busy, Func<Task<T>> body)
    {
        await _op.WaitAsync();
        try
        {
            PublishBusy(busy);
            return await body();
        }
        finally
        {
            _busy = null;
            _op.Release();
            Wake();
        }
    }

    public async Task StartHotspotAsync()
    {
        DesiredOn = true;
        _failures = 0;
        _nextRetry = DateTime.MinValue;
        _lastError = null;
        _lastDongleProblem = null;
        Logger.Info(Loc.T("watchdog.userClickedStartHotspot"));
        Wake();
        await Task.CompletedTask;
    }

    public Task StopHotspotAsync() => RunOpAsync(Loc.T("watchdog.stoppingHotspot"), async () =>
    {
        DesiredOn = false;
        Logger.Info(Loc.T("watchdog.userClickedStopHotspotWatchdog"));
        _dhcp.StopServer(Loc.T("watchdog.stoppedByUser"));
        try { await _hc.StopAsync(); }
        catch (Exception ex) { Logger.Error(Loc.T("watchdog.failedStopHotspot") + ex.Message); }
        return true;
    });

    /// <summary>保存并应用热点名/密码/频段/加密。返回错误说明，成功返回 null。</summary>
    public Task<string?> ApplyHotspotSettingsAsync(string ssid, string password, string band, string auth) => RunOpAsync<string?>(Loc.T("watchdog.applyingHotspotSettings"), async () =>
    {
        var err = AppSettings.Validate(ssid, password);
        if (err != null) return err;
        var oldPw = _s.Password;
        _s.Ssid = ssid.Trim();
        _s.Password = password;
        _s.Band = band;
        _s.Auth = auth;
        Logger.RegisterSecret(password);
        if (oldPw != password) Logger.ForgetSecret(oldPw);
        SaveSettings();
        try
        {
            bool changed = await _hc.ApplyConfigAsync(_s);
            if (!changed) { Logger.Info(Loc.T("watchdog.hotspotSettingsUnchanged")); return null; }
            Logger.Ok(Loc.T("watchdog.hotspotSettingsSavedWindowsMobile"));
            if (DesiredOn)
            {
                await _hc.StartAsync(_s);
                _beacon = null;
                _beaconDueAt = DateTime.UtcNow.AddSeconds(8);
                Logger.Ok(Loc.T("watchdog.hotspotRestartedWithNewSettings"));
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(Loc.T("watchdog.failedApplyHotspotSettings") + ex.Message);
            return ex.Message;
        }
    });

    /// <summary>
    /// 允许 / 禁止连接的设备访问互联网（internet.allow），立即生效，不重开热点：
    /// 关 = 删除本软件的 NAT、关闭热点网卡的转发、DHCP 不再下发 DNS；开 = 恢复 NAT 和转发。返回给用户看的结果。
    /// </summary>
    public Task<(bool Ok, string Message)> SetInternetAllowedAsync(bool allow) => RunOpAsync(allow ? Loc.T("watchdog.allowingInternetAccess") : Loc.T("watchdog.switchingLanOnly"), async () =>
    {
        bool changed = _s.Internet.Allow != allow;
        _s.Internet.Allow = allow;
        SaveSettings();
        _nat.Invalidate();
        if (changed) Logger.Info(Loc.T("watchdog.setting") + InternetStrings.StatusText(allow));
        await Task.CompletedTask;
        var snap = _hc.GetSnapshot();
        bool on = snap.State == HotspotState.On;
        if (!on)
        {
            // 热点没开：仅局域网时也顺手删掉本软件的 NAT
            if (!allow)
            {
                var st0 = _nat.Reconcile(null, null, false, _isAdmin);
                if (!st0.Ok) return (false, InternetStrings.StatusText(allow) + Loc.T("common.period") + st0.Text);
            }
            return (true, InternetStrings.StatusText(allow) + Loc.T("common.period") + InternetStrings.SavedOnlyNotRunning);
        }
        var dongle = DongleInfo.Find();
        var go = NetworkInfo.FindGoAdapter(dongle?.Mac);
        var uplink = NetworkInfo.GetUplink(snap.UplinkAdapterId);
        var dhcpSt = _dhcp.Reconcile(go, uplink, on, _isAdmin);
        var natSt = go != null || !allow
            ? _nat.Reconcile(go, uplink, dhcpSt.Provider == DhcpProvider.Ics, _isAdmin)
            : new NatStatus(NatProvider.None, Loc.T("watchdog.hotspotAdapterIsnTReady"), false);
        Logger.Write(natSt.Ok ? LogLevel.Ok : LogLevel.Warn, "NAT: " + natSt.Text);
        var msg = allow ? InternetStrings.AppliedOn : InternetStrings.AppliedOff;
        bool ok = natSt.Ok;
        if (!natSt.Ok) msg += "\n" + natSt.Text;
        if (!allow && dhcpSt.Provider == DhcpProvider.Ics && natSt.Ok) { msg += "\n" + InternetStrings.IcsHint; ok = false; }
        return (ok, msg);
    });

    /// <summary>设置“钉住到网络（实验）”。空 = 不钉住（立即断开 D-Link 的钉住连接，热点不动）；否则下次启动热点时生效。</summary>
    public Task<string> SetPinSsidAsync(string? ssid) => RunOpAsync(Loc.T("watchdog.savingChannelSettings"), async () =>
    {
        ssid = (ssid ?? "").Trim();
        var old = (_s.Channel.PinSsid ?? "").Trim();
        _s.Channel.PinSsid = ssid;
        SaveSettings();
        _pin.SettingChanged();
        await Task.CompletedTask;
        if (old != ssid) Logger.Info(Loc.T("watchdog.settingPinNetwork") + (ssid.Length == 0 ? ChannelStrings.PinAuto : ssid));
        if (ssid.Length == 0)
        {
            if (_pin.Active) { _pin.Release(Loc.T("watchdog.setNoPin")); return ChannelStrings.PinReleasedNow; }
            return ChannelStrings.PinSavedOff;
        }
        bool on = _hc.GetSnapshot().State == HotspotState.On;
        if (on && !(_pin.Active && _pin.Status.Ssid == ssid)) return ChannelStrings.PinNextStart;
        return string.Format(ChannelStrings.PinSavedNow, ssid);
    });

    /// <summary>用内置驱动安装 / 重新安装 D-Link 驱动（需要管理员）。</summary>
    public Task<DriverInstallResult> InstallDriverAsync(DriverPackage package, IProgress<string>? progress) => RunOpAsync(DriverStrings.Busy, async () =>
    {
        Logger.Info(Loc.T("watchdog.userClicked") + (_driver.IsInstalled ? DriverStrings.ButtonReinstall : DriverStrings.ButtonInstall));
        var r = await DriverInstaller.InstallAsync(package, _store.DataDirectory, progress, CancellationToken.None);
        DongleInfo.InvalidateUsbProbe();
        _nextRetry = DateTime.MinValue;
        _failures = 0;
        _lastDongleProblem = null;
        if (r.Ok) _lastError = null;
        TrackDriver(DongleInfo.Find());
        return r;
    });

    /// <summary>扫描附近 5GHz Wi-Fi（用电脑的其它无线网卡；只有 D-Link 时热点开着不扫描）。</summary>
    public async Task<ChannelReport> ScanChannelsAsync()
    {
        var st = Status;
        bool on = st.Hotspot?.State == HotspotState.On;
        var r = await ChannelInfo.ScanAsync(st.Go?.Mac, st.Dongle?.Guid, allowDongle: !on, _s.Ssid);
        Wake();
        return r;
    }

    /// <summary>修复网络：关热点 → 处理 Default Switch / 幽灵记录 → 重启 ICS → 开热点 → 检查。</summary>
    public Task<List<string>> RepairNetworkAsync(Func<string, bool> confirm) => RunOpAsync(Loc.T("watchdog.repairingNetwork"), async () =>
    {
        var report = new List<string>();
        if (!_isAdmin) { report.Add(Loc.T("watchdog.appMustRunAsAdministrator")); return report; }
        Logger.Info(Loc.T("watchdog.startingNetworkRepair"));
        // 1. 关热点
        _dhcp.StopServer(Loc.T("watchdog.repairNetwork"));
        try { await _hc.StopAsync(); report.Add(Loc.T("watchdog.hotspotStopped2")); }
        catch (Exception ex) { report.Add(Loc.T("watchdog.failedStopHotspot") + ex.Message); }
        await Task.Delay(2000);
        // 2–4
        report.AddRange(await IcsRepair.RepairIcsAsync(_s, SaveSettings, confirm));
        // 5. 开热点
        DesiredOn = true;
        _failures = 0;
        try
        {
            await _hc.StartAsync(_s);
            report.Add(Loc.T("watchdog.hotspotRestarted"));
        }
        catch (Exception ex) { report.Add(Loc.T("watchdog.failedRestartHotspotWatchdogWill", ex.Message)); }
        // 6. 检查
        GoAdapterInfo? go = null;
        var dongle = DongleInfo.Find();
        for (int i = 0; i < 20 && !(go?.HasUsableIp ?? false); i++) { await Task.Delay(1000); go = NetworkInfo.FindGoAdapter(dongle?.Mac); }
        await Task.Delay(4000);
        if (go?.Ip != null)
        {
            var binds = IcsRepair.DhcpBindings();
            if (binds.Any(b => b.Ip.Equals(go.Ip))) report.Add(Loc.T("watchdog.dhcpWindowsIcsBackBound", go.Ip));
            else report.Add(Loc.T("watchdog.dhcpWindowsIcsStillNot", (binds.Count == 0 ? Loc.T("watchdog.notListening") : string.Join(Loc.T("common.enumSep"), binds.Select(b => Loc.T("common.nameBracket", b.Ip, b.Adapter))))));
            report.Add(MacUtil.SameRadio(go.Mac, dongle?.Mac) ? Loc.T("watchdog.hotspotDLinkGoMac", go.Mac, go.Ip) : Loc.T("watchdog.hotspotNotDLinkGo", go.Mac));
        }
        else report.Add(Loc.T("watchdog.hotspotAdapterDidnTGet"));
        _beacon = null;
        _beaconDueAt = DateTime.UtcNow.AddSeconds(8);
        foreach (var line in report) Logger.Info(Loc.T("watchdog.repairNetworkResults") + line);
        return report;
    });

    public Task<List<StepResult>> OptimizeAsync(Func<string, bool> confirm) => RunOpAsync(Loc.T("watchdog.runningOneClickOptimize"), async () =>
        await SystemOptimizer.RunAsync(DongleInfo.Find(), _exePath, _s, SaveSettings, confirm));

    /// <summary>还原：关闭热点，撤销本软件对系统的改动（WinNAT、转发、禁用的网卡、OVRService、防火墙、自启动）。</summary>
    public Task<List<string>> RevertAsync() => RunOpAsync(Loc.T("watchdog.revertingSystemSettings"), async () =>
    {
        DesiredOn = false;
        _dhcp.StopServer(Loc.T("watchdog.revert"));
        try { await _hc.StopAsync(); } catch { }
        return await Reverter.RunAsync(_s, SaveSettings);
    });

    /// <summary>程序退出。stopHotspot = 同时关闭热点。注意：本软件提供的 DHCP 会随程序退出而停止。</summary>
    public async Task ShutdownAsync(bool stopHotspot)
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_loop != null) await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { _joinMonitor?.Dispose(); } catch { }
        _dhcp.StopServer(Loc.T("watchdog.appExiting"));
        if (stopHotspot)
        {
            try { await _hc.StopAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception ex) { Logger.Warn(Loc.T("watchdog.failedStopHotspotExit") + ex.Message); }
        }
        else Logger.Info(Loc.T("watchdog.appExitingHotspotStays"));
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _joinMonitor?.Dispose(); } catch { }
        _dhcp.Dispose();
    }
}

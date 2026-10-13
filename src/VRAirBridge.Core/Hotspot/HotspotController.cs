using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace VRAirBridge.Core.Hotspot;

public enum HotspotState { Unknown, On, Off, InTransition, NoUplink, Error }

public sealed record TetheringClientInfo(string Mac, IReadOnlyList<string> HostNames);

public sealed record HotspotSnapshot(
    HotspotState State,
    string? Ssid,
    string? BandText,
    string? AuthText,
    string? UplinkName,
    string? UplinkAdapterId,
    int ClientCount,
    int MaxClients,
    IReadOnlyList<TetheringClientInfo> Clients,
    string? CapabilityProblem,
    string? Error);

public sealed class HotspotException : Exception
{
    public HotspotException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 通过 Windows 自带“移动热点”的 WinRT API（NetworkOperatorTetheringManager）控制热点。
/// 不需要管理员权限。热点由 Windows 开在 Wi-Fi Direct 虚拟网卡上（我们只用 D-Link）。
/// </summary>
public sealed class HotspotController
{
    public sealed record Context(NetworkOperatorTetheringManager Manager, ConnectionProfile Profile, string UplinkName, string? UplinkAdapterId);

    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(60);

    /// <summary>实验功能“钉住信道”（可为 null = 不支持）。由看门狗设置。</summary>
    public ChannelPin? Pin { get; set; }

    /// <summary>取当前上网连接（上行），并据此创建热点管理对象。每次调用都重新获取，避免服务重启后对象失效。</summary>
    public Context GetContext()
    {
        ConnectionProfile? profile = null;
        try { profile = NetworkInformation.GetInternetConnectionProfile(); } catch { }
        if (profile == null)
        {
            try
            {
                profile = NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
            }
            catch { }
        }
        if (profile == null)
            throw new HotspotException(Loc.T("hotspot.noNetworkConnectionSharePc"));
        string? adapterId = null;
        try { adapterId = profile.NetworkAdapter?.NetworkAdapterId.ToString(); } catch { }
        var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        return new Context(mgr, profile, profile.ProfileName ?? "?", adapterId);
    }

    public static string BandText(TetheringWiFiBand b) => b switch
    {
        TetheringWiFiBand.FiveGigahertz => "5 GHz",
        TetheringWiFiBand.TwoPointFourGigahertz => "2.4 GHz",
        TetheringWiFiBand.SixGigahertz => "6 GHz",
        _ => Loc.T("hotspot.auto"),
    };

    public static TetheringWiFiBand BandFromSetting(string? s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("5")) return TetheringWiFiBand.FiveGigahertz;
        if (s.StartsWith("2")) return TetheringWiFiBand.TwoPointFourGigahertz;
        if (s.StartsWith("6")) return TetheringWiFiBand.SixGigahertz;
        return TetheringWiFiBand.Auto;
    }

    public static TetheringWiFiAuthenticationKind AuthFromSetting(string? s)
    {
        s = (s ?? "").Trim().ToUpperInvariant();
        if (s.Contains("TRANS")) return TetheringWiFiAuthenticationKind.Wpa3TransitionMode;
        if (s.Contains('3')) return TetheringWiFiAuthenticationKind.Wpa3;
        return TetheringWiFiAuthenticationKind.Wpa2;
    }

    public static string AuthText(TetheringWiFiAuthenticationKind k) => k switch
    {
        TetheringWiFiAuthenticationKind.Wpa3 => "WPA3",
        TetheringWiFiAuthenticationKind.Wpa3TransitionMode => Loc.T("hotspot.wpa2Wpa3Transition"),
        _ => "WPA2",
    };

    public static string StateText(HotspotState s) => s switch
    {
        HotspotState.On => Loc.T("hotspot.running"),
        HotspotState.Off => Loc.T("hotspot.off"),
        HotspotState.InTransition => Loc.T("hotspot.switching"),
        HotspotState.NoUplink => Loc.T("hotspot.noNetworkShare"),
        HotspotState.Error => Loc.T("hotspot.error"),
        _ => Loc.T("hotspot.unknown"),
    };

    private static HotspotState Map(TetheringOperationalState s) => s switch
    {
        TetheringOperationalState.On => HotspotState.On,
        TetheringOperationalState.Off => HotspotState.Off,
        TetheringOperationalState.InTransition => HotspotState.InTransition,
        _ => HotspotState.Unknown,
    };

    public static string? CapabilityProblem(ConnectionProfile profile)
    {
        try
        {
            var cap = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            return cap switch
            {
                TetheringCapability.Enabled => null,
                TetheringCapability.DisabledByGroupPolicy => Loc.T("hotspot.mobileHotspotDisabledByGroup"),
                TetheringCapability.DisabledByHardwareLimitation => Loc.T("hotspot.hardwareDoesnTSupportMobile"),
                TetheringCapability.DisabledByOperator => Loc.T("hotspot.mobileHotspotDisabledByOperator"),
                TetheringCapability.DisabledBySku => Loc.T("hotspot.windowsEditionDoesnTSupport"),
                TetheringCapability.DisabledByRequiredAppNotInstalled => Loc.T("hotspot.requiredSystemComponentMissing"),
                TetheringCapability.DisabledBySystemCapability => Loc.T("hotspot.systemBlocksMobileHotspotLet"),
                _ => Loc.T("hotspot.mobileHotspotUnavailableUnknownReason", cap),
            };
        }
        catch { return null; }
    }

    /// <summary>读取 Windows 移动热点当前保存的热点名和密码（读不到返回 null）。</summary>
    public (string Ssid, string Passphrase)? TryReadCurrentConfig()
    {
        try
        {
            var cfg = GetContext().Manager.GetCurrentAccessPointConfiguration();
            return (cfg.Ssid ?? "", cfg.Passphrase ?? "");
        }
        catch { return null; }
    }

    public HotspotSnapshot GetSnapshot()
    {
        Context ctx;
        try { ctx = GetContext(); }
        catch (HotspotException ex)
        {
            return new HotspotSnapshot(HotspotState.NoUplink, null, null, null, null, null, 0, 0, Array.Empty<TetheringClientInfo>(), null, ex.Message);
        }
        catch (Exception ex)
        {
            return new HotspotSnapshot(HotspotState.Error, null, null, null, null, null, 0, 0, Array.Empty<TetheringClientInfo>(), null, ex.Message);
        }
        try
        {
            var mgr = ctx.Manager;
            var state = Map(mgr.TetheringOperationalState);
            string? ssid = null, band = null, auth = null;
            try
            {
                var cfg = mgr.GetCurrentAccessPointConfiguration();
                ssid = cfg.Ssid;
                try { band = BandText(cfg.Band); } catch { }
                try { auth = AuthText(cfg.AuthenticationKind); } catch { }
            }
            catch { }
            var clients = new List<TetheringClientInfo>();
            int count = 0, max = 0;
            try { count = (int)mgr.ClientCount; max = (int)mgr.MaxClientCount; } catch { }
            if (state == HotspotState.On)
            {
                try
                {
                    foreach (var c in mgr.GetTetheringClients())
                    {
                        var names = new List<string>();
                        try { names.AddRange(c.HostNames.Select(h => h.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n))); } catch { }
                        clients.Add(new TetheringClientInfo(c.MacAddress ?? "", names));
                    }
                }
                catch { }
            }
            return new HotspotSnapshot(state, ssid, band, auth, ctx.UplinkName, ctx.UplinkAdapterId, count, max, clients, CapabilityProblem(ctx.Profile), null);
        }
        catch (Exception ex)
        {
            return new HotspotSnapshot(HotspotState.Error, null, null, null, ctx.UplinkName, ctx.UplinkAdapterId, 0, 0, Array.Empty<TetheringClientInfo>(), null, ex.Message);
        }
    }

    /// <summary>把配置（SSID/密码/频段/加密）写入 Windows 热点。返回是否有改动。热点正在运行且有改动时会先关掉。</summary>
    public async Task<bool> ApplyConfigAsync(AppSettings s, CancellationToken ct = default)
    {
        var ctx = GetContext();
        var mgr = ctx.Manager;
        var cur = mgr.GetCurrentAccessPointConfiguration();
        var wantBand = BandFromSetting(s.Band);
        var wantAuth = AuthFromSetting(s.Auth);
        bool authApi = true;
        TetheringWiFiAuthenticationKind curAuth = TetheringWiFiAuthenticationKind.Wpa2;
        try { curAuth = cur.AuthenticationKind; } catch { authApi = false; }

        var changes = new List<string>();
        if (!string.Equals(cur.Ssid, s.Ssid, StringComparison.Ordinal)) changes.Add(Loc.T("hotspot.hotspotName", cur.Ssid, s.Ssid));
        if (!string.Equals(cur.Passphrase, s.Password, StringComparison.Ordinal)) changes.Add(Loc.T("hotspot.password"));
        TetheringWiFiBand curBand = TetheringWiFiBand.Auto;
        bool bandApi = true;
        try { curBand = cur.Band; } catch { bandApi = false; }
        if (bandApi && curBand != wantBand) changes.Add(Loc.T("hotspot.band", BandText(curBand), BandText(wantBand)));
        if (authApi && curAuth != wantAuth) changes.Add(Loc.T("hotspot.security", AuthText(curAuth), AuthText(wantAuth)));
        if (changes.Count == 0) return false;

        Logger.Info(Loc.T("hotspot.updatingHotspotConfiguration") + string.Join(Loc.T("common.listSep"), changes));
        if (mgr.TetheringOperationalState == TetheringOperationalState.On)
        {
            Logger.Info(Loc.T("hotspot.stoppingRunningHotspotApplyNew"));
            await mgr.StopTetheringAsync().AsTask(ct).WaitAsync(OpTimeout, ct);
            await Task.Delay(1500, ct);
        }
        cur.Ssid = s.Ssid;
        cur.Passphrase = s.Password;
        if (bandApi)
        {
            try
            {
                // IsBandSupported is only a pre-check and is often wrong for the dongle (5 GHz works and the beacon
                // confirms it), so this is information, not a warning; a real rejection is handled below (falls back to Auto)
                if (wantBand != TetheringWiFiBand.Auto && !cur.IsBandSupported(wantBand))
                    Logger.Info(Loc.T("hotspot.systemReportsWiFiAdapter", BandText(wantBand)));
            }
            catch { }
            cur.Band = wantBand;
        }
        if (authApi)
        {
            try
            {
                if (cur.IsAuthenticationKindSupported(wantAuth)) cur.AuthenticationKind = wantAuth;
                else Logger.Warn(Loc.T("hotspot.systemDoesnTSupportSecurity", AuthText(wantAuth), AuthText(curAuth)));
            }
            catch (Exception ex) { Logger.Warn(Loc.T("hotspot.cannotSetSecurityType") + ex.Message); }
        }
        try
        {
            await mgr.ConfigureAccessPointAsync(cur).AsTask(ct).WaitAsync(OpTimeout, ct);
        }
        catch (Exception ex) when (bandApi && wantBand != TetheringWiFiBand.Auto && ex is not OperationCanceledException)
        {
            Logger.Warn(Loc.T("hotspot.configuringHotspotFailedRetryingWith", BandText(wantBand), ex.Message));
            cur.Band = TetheringWiFiBand.Auto;
            await mgr.ConfigureAccessPointAsync(cur).AsTask(ct).WaitAsync(OpTimeout, ct);
        }
        return true;
    }

    public async Task StartAsync(AppSettings s, CancellationToken ct = default)
    {
        bool pinned = false;
        if (Pin != null && !IsOnQuiet())
            pinned = await Pin.BeforeStartAsync(s, ct);
        try
        {
            await StartCoreAsync(s, ct);
        }
        catch (Exception ex) when (pinned && ex is not OperationCanceledException)
        {
            // 钉住状态下没开起来：断开 D-Link 的连接，不钉住再开一次
            Logger.Warn(Loc.T("hotspot.hotspotFailedStartWhilePinning") + ex.Message);
            Pin!.Release(Loc.T("hotspot.hotspotFailedStart"));
            Pin.Suppress((s.Channel.PinSsid ?? "").Trim(), ChannelStrings.PinFallback);
            await StartCoreAsync(s, ct);
        }
    }

    private bool IsOnQuiet()
    {
        try { return GetContext().Manager.TetheringOperationalState == TetheringOperationalState.On; }
        catch { return false; }
    }

    private async Task StartCoreAsync(AppSettings s, CancellationToken ct)
    {
        await ApplyConfigAsync(s, ct);
        var ctx = GetContext();
        var problem = CapabilityProblem(ctx.Profile);
        if (problem != null) Logger.Warn(Loc.T("hotspot.systemReports") + problem);
        var mgr = ctx.Manager;
        if (mgr.TetheringOperationalState == TetheringOperationalState.On)
        {
            Logger.Info(Loc.T("hotspot.hotspotAlreadyRunning"));
            return;
        }
        Logger.Info(Loc.T("hotspot.startingHotspotNameBandUplink", s.Ssid, s.Band, ctx.UplinkName));
        NetworkOperatorTetheringOperationResult r;
        try
        {
            r = await mgr.StartTetheringAsync().AsTask(ct).WaitAsync(OpTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new HotspotException(Loc.T("hotspot.startingHotspotTimedOutNo"));
        }
        if (r.Status != TetheringOperationStatus.Success)
            throw new HotspotException(Loc.T("hotspot.hotspotFailedStart2") + StatusText(r.Status) + (string.IsNullOrWhiteSpace(r.AdditionalErrorMessage) ? "" : " - " + r.AdditionalErrorMessage));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            var ctx = GetContext();
            var mgr = ctx.Manager;
            if (mgr.TetheringOperationalState == TetheringOperationalState.Off)
            {
                Logger.Info(Loc.T("hotspot.hotspotWasNotRunning"));
                return;
            }
            var r = await mgr.StopTetheringAsync().AsTask(ct).WaitAsync(OpTimeout, ct);
            if (r.Status == TetheringOperationStatus.Success) Logger.Ok(Loc.T("hotspot.hotspotStopped"));
            else Logger.Warn(Loc.T("hotspot.stopHotspot", StatusText(r.Status)) + r.AdditionalErrorMessage);
        }
        finally
        {
            // 热点关了：断开钉住用的 D-Link 客户端连接，恢复跃点数
            Pin?.Release(Loc.T("hotspot.hotspotStopped"));
            ChannelInfo.ForgetGoChannel();
        }
    }

    public static string StatusText(TetheringOperationStatus s) => s switch
    {
        TetheringOperationStatus.Success => Loc.T("hotspot.success"),
        TetheringOperationStatus.WiFiDeviceOff => Loc.T("hotspot.wiFiRadioOffCheck"),
        TetheringOperationStatus.OperationInProgress => Loc.T("hotspot.anotherHotspotOperationProgress"),
        TetheringOperationStatus.EntitlementCheckFailure => Loc.T("hotspot.operatorEntitlementCheckFailed"),
        TetheringOperationStatus.EntitlementCheckTimeout => Loc.T("hotspot.operatorEntitlementCheckTimedOut"),
        TetheringOperationStatus.MobileBroadbandDeviceOff => Loc.T("hotspot.mobileBroadbandDeviceOff"),
        TetheringOperationStatus.BluetoothDeviceOff => Loc.T("hotspot.bluetoothOff"),
        TetheringOperationStatus.NetworkLimitedConnectivity => Loc.T("hotspot.uplinkNetworkConnectionRestricted"),
        _ => s.ToString(),
    };

    /// <summary>“无设备连接时自动关闭热点”是否开启（null = 系统不支持查询）。</summary>
    public static bool? IsNoConnectionsTimeoutEnabled()
    {
        try { return NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled(); }
        catch { return null; }
    }

    public static async Task<bool> DisableNoConnectionsTimeoutAsync()
    {
        try
        {
            await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(Loc.T("hotspot.failedTurnOffTurnOff") + ex.Message);
            return false;
        }
    }
}

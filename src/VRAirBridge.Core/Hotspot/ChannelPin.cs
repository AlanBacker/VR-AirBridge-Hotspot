using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;
using VRAirBridge.Core.Wlan;

namespace VRAirBridge.Core.Hotspot;

public enum PinPhase { Off, Pinned, Verified, Unverified, Suppressed }

public sealed record PinStatus(PinPhase Phase, string? Ssid, int? StaChannel, string Text)
{
    public bool Active => Phase is PinPhase.Pinned or PinPhase.Verified or PinPhase.Unverified;
}

/// <summary>
/// 实验功能“钉住信道”（设置 channel.pinSsid，默认关闭）：
/// 开热点前让 D-Link 以普通客户端身份连上指定的 5GHz Wi-Fi（驱动 MultiChannelFcsMode=0 时，热点只能和这个连接同信道），
/// 于是热点会跟着那个网络的信道走。步骤：
/// (a) D-Link 上没有这个网络的配置文件时，从电脑自己的无线网卡上复制一份（带明文密钥的 XML 只在内存里，不记日志、不落盘），改成“手动连接”；
/// (b) 把 D-Link 的 IPv4 跃点数设为 500（不抢电脑自己的上网路由）；
/// (c) WlanConnect，最多等 15 秒。
/// 之后由看门狗核对：热点仍在 D-Link 上、信标信道 == D-Link 连接的信道；不满足就回退（关热点 → 断开 D-Link → 不钉住重开）。
/// 热点关闭时断开 D-Link 的连接并恢复跃点数。
/// </summary>
public sealed class ChannelPin
{
    public const int PinMetric = 500;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan VerifyWindow = TimeSpan.FromSeconds(60);

    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private readonly object _lock = new();
    private PinPhase _phase = PinPhase.Off;
    private string? _ssid;
    private int? _staChannel;
    private DateTime _pinnedAt;
    private Guid _dongle;
    private string? _suppressedFor;
    private string? _suppressReason;

    /// <summary>需要告诉用户的事（回退、权限不足等）。</summary>
    public event Action<string>? Notice;

    public ChannelPin(Func<AppSettings> settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    public bool Active { get { lock (_lock) return _phase is PinPhase.Pinned or PinPhase.Verified or PinPhase.Unverified; } }
    public int? StaChannel => _staChannel;

    public PinStatus Status
    {
        get
        {
            lock (_lock)
            {
                var ch = _staChannel?.ToString(CultureInfo.InvariantCulture) ?? "?";
                return _phase switch
                {
                    PinPhase.Pinned => new PinStatus(_phase, _ssid, _staChannel, string.Format(ChannelStrings.PinActive, _ssid, ch)),
                    PinPhase.Verified => new PinStatus(_phase, _ssid, _staChannel, string.Format(ChannelStrings.PinVerified, _ssid, ch)),
                    PinPhase.Unverified => new PinStatus(_phase, _ssid, _staChannel, string.Format(ChannelStrings.PinUnverified, _ssid, ch)),
                    PinPhase.Suppressed => new PinStatus(_phase, _suppressedFor, null, string.Format(ChannelStrings.PinSuppressed, _suppressReason)),
                    _ => new PinStatus(_phase, null, null, ChannelStrings.PinOff),
                };
            }
        }
    }

    /// <summary>用户改了“钉住到网络”：清除本次运行的回退记录。</summary>
    public void SettingChanged()
    {
        lock (_lock)
        {
            _suppressedFor = null;
            _suppressReason = null;
            if (_phase == PinPhase.Suppressed) _phase = PinPhase.Off;
        }
    }

    /// <summary>本次运行不再对这个 SSID 钉住（直到设置改变或软件重启），并通知用户。</summary>
    public void Suppress(string ssid, string reason)
    {
        lock (_lock)
        {
            _suppressedFor = ssid;
            _suppressReason = reason;
            _phase = PinPhase.Suppressed;
        }
        Logger.Warn(Loc.T("pin.channelPin") + reason);
        try { Notice?.Invoke(reason); } catch { }
    }

    // ================================================================ 开热点前

    /// <summary>开热点前调用。不会抛异常（取消除外）；返回 true = 已钉住。</summary>
    public async Task<bool> BeforeStartAsync(AppSettings s, CancellationToken ct)
    {
        var ssid = (s.Channel.PinSsid ?? "").Trim();
        try
        {
            return await BeforeStartCoreAsync(s, ssid, ct);
        }
        catch (OperationCanceledException) { Release(Loc.T("pin.cancelled")); throw; }
        catch (Exception ex)
        {
            Logger.Warn(Loc.T("pin.channelPinError") + ex.Message);
            Release(Loc.T("pin.error"));
            if (ssid.Length > 0) Suppress(ssid, ChannelStrings.PinFallback);
            return false;
        }
    }

    private async Task<bool> BeforeStartCoreAsync(AppSettings s, string ssid, CancellationToken ct)
    {
        if (ssid.Length == 0)
        {
            if (Active) Release(Loc.T("pin.setNoPin"));
            return false;
        }
        lock (_lock) if (_suppressedFor == ssid) return false;
        if (Active && _ssid != ssid) Release(Loc.T("pin.pinnedNetworkChanged"));
        if (!AdminUtil.IsAdministrator()) { Suppress(ssid, ChannelStrings.PinNeedAdmin); return false; }

        var dongle = DongleInfo.Find();
        if (dongle == null || !Guid.TryParse(dongle.Guid, out var dg)) return false;
        _dongle = dg;

        var conn = WlanNative.QueryConnection(dg);
        bool alreadyConnected = conn is { State: 1 } && conn.Ssid == ssid;
        string? profileName = alreadyConnected ? conn!.ProfileName : EnsureProfileOnDongle(dg, ssid, s);
        if (profileName == null) { Suppress(ssid, string.Format(ChannelStrings.PinNoProfile, ssid)); return false; }

        SetMetric(dongle.IfIndex, s);

        if (!alreadyConnected)
        {
            Logger.Info(string.Format(ChannelStrings.PinConnecting, ssid));
            WlanNative.Connect(dg, profileName);
            var until = DateTime.UtcNow + ConnectTimeout;
            while (true)
            {
                await Task.Delay(1000, ct);
                var c = WlanNative.QueryConnection(dg);
                if (c is { State: 1 } && c.Ssid == ssid) break;
                if (DateTime.UtcNow >= until)
                {
                    ForceRelease(dg, s, Loc.T("pin.connectionTimedOut"));
                    Suppress(ssid, string.Format(ChannelStrings.PinConnectFailed, ssid));
                    return false;
                }
            }
        }

        var ch = WlanNative.QueryChannel(dg);
        if (ch is int c0 && (ChannelInfo.IsDfs(c0) || c0 <= 14))
        {
            ForceRelease(dg, s, Loc.T("pin.channelNotUsable"));
            Suppress(ssid, string.Format(ChannelInfo.IsDfs(c0) ? ChannelStrings.PinDfs : ChannelStrings.Pin24, ssid, c0));
            return false;
        }
        lock (_lock)
        {
            _ssid = ssid;
            _staChannel = ch;
            _pinnedAt = DateTime.Now;
            _phase = PinPhase.Pinned;
        }
        Logger.Ok(string.Format(ChannelStrings.PinActive, ssid, ch?.ToString(CultureInfo.InvariantCulture) ?? "?"));
        return true;
    }

    /// <summary>
    /// 确保 D-Link 上有这个 SSID 的配置文件，返回配置文件名；没有、也复制不到时返回 null。
    /// 复制时用 WLAN_PROFILE_GET_PLAINTEXT_KEY 读出 XML，只在内存里改成“手动连接”后 WlanSetProfile 写给 D-Link。
    /// </summary>
    private string? EnsureProfileOnDongle(Guid dg, string ssid, AppSettings s)
    {
        var onDongle = WlanNative.GetProfileNames(dg);
        if (onDongle.Contains(ssid)) return ssid;
        foreach (var name in onDongle)
            if (ProfileSsid(WlanNative.GetProfileXml(dg, name, plaintextKey: false)) == ssid) return name;

        foreach (var iface in WlanNative.EnumInterfaces().Where(i => i.Guid != dg))
        {
            var names = new List<string>();
            var conn = WlanNative.QueryConnection(iface.Guid);
            if (conn is { State: 1 } && conn.Ssid == ssid && conn.ProfileName.Length > 0) names.Add(conn.ProfileName);
            var all = WlanNative.GetProfileNames(iface.Guid);
            if (all.Contains(ssid)) names.Add(ssid);
            foreach (var n in all.Where(n => !names.Contains(n)))
                if (ProfileSsid(WlanNative.GetProfileXml(iface.Guid, n, plaintextKey: false)) == ssid) names.Add(n);

            foreach (var name in names.Distinct())
            {
                // 注意：xml 含明文密钥，只能留在内存里
                string? xml = WlanNative.GetProfileXml(iface.Guid, name, plaintextKey: true);
                if (xml == null || ProfileSsid(xml) != ssid) continue;
                var profileName = ProfileName(xml) ?? name;
                try
                {
                    WlanNative.SetProfileXml(dg, MakeManual(xml));
                }
                catch (WlanException ex)
                {
                    Logger.Warn(Loc.T("pin.failedCopyWiFiProfile", ssid, ex.Message));
                    return null;
                }
                finally { xml = null; }
                var key = $"{dg}|{profileName}";
                if (!s.Revert.PinProfilesAdded.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    s.Revert.PinProfilesAdded.Add(key);
                    _save();
                }
                Logger.Info(Loc.T("pin.copiedWiFiProfileFrom", ssid, iface.Description));
                return profileName;
            }
        }
        return null;
    }

    // ================================================================ 核对 / 释放

    /// <summary>
    /// 看门狗每个周期调用。返回需要回退的原因（null = 正常或还没法判断）。
    /// </summary>
    public string? Verify(bool hotspotOn, bool? goOnDongle, BeaconResult? beacon)
    {
        if (!hotspotOn || !Active) return null;
        if (goOnDongle == false) return Loc.T("pin.hotspotNotRunningDLink");
        PinPhase phase; int? sta; DateTime at;
        lock (_lock) { phase = _phase; sta = _staChannel; at = _pinnedAt; }
        if (beacon != null && beacon.Time > at && sta != null)
        {
            if (beacon.Info.Channel != sta)
                return Loc.T("pin.hotspotChannelDLinkConnection", beacon.Info.Channel, sta);
            if (phase != PinPhase.Verified)
            {
                lock (_lock) _phase = PinPhase.Verified;
                Logger.Ok(string.Format(ChannelStrings.PinVerified, _ssid, sta));
            }
            return null;
        }
        if (phase == PinPhase.Pinned && DateTime.Now - at > VerifyWindow)
        {
            lock (_lock) _phase = PinPhase.Unverified;
            Logger.Info(string.Format(ChannelStrings.PinUnverified, _ssid, sta?.ToString(CultureInfo.InvariantCulture) ?? "?"));
        }
        return null;
    }

    /// <summary>热点关闭 / 回退 / 退出时调用：断开 D-Link 的客户端连接（只断本软件连上的），恢复跃点数。不会抛异常。</summary>
    public void Release(string reason)
    {
        try
        {
            bool wasActive;
            lock (_lock)
            {
                wasActive = _phase is PinPhase.Pinned or PinPhase.Verified or PinPhase.Unverified;
                if (_phase != PinPhase.Suppressed) _phase = PinPhase.Off;
                _staChannel = null;
            }
            var s = _settings();
            if (!wasActive && s.Revert.PinProfilesAdded.Count == 0 && s.Revert.PinMetricIfIndex <= 0) return;
            var dg = _dongle;
            if (dg == Guid.Empty && DongleInfo.Find() is { } d) Guid.TryParse(d.Guid, out dg);
            if (dg != Guid.Empty)
            {
                var conn = WlanNative.QueryConnection(dg);
                bool ours = wasActive || (conn != null && s.Revert.PinProfilesAdded.Contains($"{dg}|{conn.ProfileName}", StringComparer.OrdinalIgnoreCase));
                if (conn is { State: 1 } && ours)
                {
                    WlanNative.Disconnect(dg);
                    Logger.Info(Loc.T("pin.channelPinDisconnectedDLink", reason));
                }
            }
            RestoreMetric(s);
        }
        catch (Exception ex) { Logger.Warn(Loc.T("pin.errorWhileReleasingChannelPin") + ex.Message); }
    }

    private void ForceRelease(Guid dg, AppSettings s, string reason)
    {
        try { WlanNative.Disconnect(dg); } catch { }
        Logger.Info(Loc.T("pin.channelPinDisconnectedDLink", reason));
        RestoreMetric(s);
        lock (_lock) { if (_phase != PinPhase.Suppressed) _phase = PinPhase.Off; _staChannel = null; }
    }

    // ================================================================ 跃点数

    private void SetMetric(int ifIndex, AppSettings s)
    {
        if (ifIndex <= 0) return;
        if (s.Revert.PinMetricIfIndex != ifIndex)
        {
            var (auto, metric) = ReadMetric(ifIndex);
            s.Revert.PinMetricIfIndex = ifIndex;
            s.Revert.PinMetricWasAutomatic = auto ?? true;
            s.Revert.PinMetricPrevious = metric ?? 0;
            _save();
        }
        var r = ProcessRunner.PowerShell(
            $"try {{ Set-NetIPInterface -InterfaceIndex {ifIndex} -AddressFamily IPv4 -InterfaceMetric {PinMetric} -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 30000);
        if (r.Success) Logger.Info(Loc.T("pin.channelPinDLinkIpv4", PinMetric));
        else Logger.Warn(Loc.T("pin.failedSetDLinkMetric") + r.Combined.Replace("ERR:", "").Trim());
    }

    private void RestoreMetric(AppSettings s)
    {
        var lines = RestoreMetricCore(s);
        foreach (var line in lines) Logger.Info(Loc.T("pin.channelPin2") + line);
        if (lines.Count > 0) _save();
    }

    private static List<string> RestoreMetricCore(AppSettings s)
    {
        var res = new List<string>();
        int idx = s.Revert.PinMetricIfIndex;
        if (idx <= 0) return res;
        var arg = s.Revert.PinMetricWasAutomatic ? "-AutomaticMetric Enabled" : $"-InterfaceMetric {s.Revert.PinMetricPrevious}";
        var r = ProcessRunner.PowerShell(
            $"try {{ Set-NetIPInterface -InterfaceIndex {idx} -AddressFamily IPv4 {arg} -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 30000);
        res.Add(r.Success ? Loc.T("pin.restoredDLinkIpv4Metric") : Loc.T("pin.failedRestoreDLinkMetric"));
        s.Revert.PinMetricIfIndex = 0;
        return res;
    }

    private static (bool? Automatic, int? Metric) ReadMetric(int ifIndex)
    {
        try
        {
            using var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"), new ObjectQuery(
                $"SELECT InterfaceMetric, AutomaticMetric FROM MSFT_NetIPInterface WHERE InterfaceIndex = {ifIndex} AND AddressFamily = 2"));
            foreach (ManagementBaseObject o in q.Get())
                using (o)
                    return (o["AutomaticMetric"] is { } a ? Convert.ToInt32(a, CultureInfo.InvariantCulture) != 0 : null,
                            o["InterfaceMetric"] is { } m ? Convert.ToInt32(m, CultureInfo.InvariantCulture) : null);
        }
        catch (Exception ex) { Logger.Debug(Loc.T("pin.failedReadMetric") + ex.Message); }
        return (null, null);
    }

    /// <summary>还原：断开 D-Link 的钉住连接，删除复制过去的配置文件，恢复跃点数。</summary>
    public static List<string> Revert(AppSettings s)
    {
        var report = new List<string>();
        foreach (var entry in s.Revert.PinProfilesAdded.ToList())
        {
            var parts = entry.Split('|', 2);
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var g)) { s.Revert.PinProfilesAdded.Remove(entry); continue; }
            var conn = WlanNative.QueryConnection(g);
            if (conn is { State: 1 } && conn.ProfileName == parts[1]) WlanNative.Disconnect(g);
            report.Add(WlanNative.DeleteProfile(g, parts[1])
                ? Loc.T("pin.deletedCopiedWiFiProfile", parts[1])
                : Loc.T("pin.wiFiProfileDLink", parts[1]));
            s.Revert.PinProfilesAdded.Remove(entry);
        }
        report.AddRange(RestoreMetricCore(s));
        return report;
    }

    // ================================================================ 配置文件 XML（纯函数，可单元测试；绝不记录 XML 内容）

    /// <summary>配置文件 XML 里的 SSID（SSIDConfig/SSID/name）。</summary>
    public static string? ProfileSsid(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SSIDConfig")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "SSID")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
        }
        catch { return null; }
    }

    /// <summary>配置文件名（根元素下的 name）。</summary>
    public static string? ProfileName(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try { return XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value; }
        catch { return null; }
    }

    /// <summary>把 connectionMode 改成 manual（D-Link 不会自己去连这个网络），其余原样保留。</summary>
    public static string MakeManual(string xml)
    {
        var re = new Regex(@"<connectionMode>\s*auto\s*</connectionMode>", RegexOptions.IgnoreCase);
        if (re.IsMatch(xml)) return re.Replace(xml, "<connectionMode>manual</connectionMode>", 1);
        if (Regex.IsMatch(xml, @"<connectionMode>", RegexOptions.IgnoreCase)) return xml;
        // 没有 connectionMode 元素：插在 MSM 前面
        var idx = xml.IndexOf("<MSM", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? xml : xml.Insert(idx, "<connectionMode>manual</connectionMode>");
    }
}

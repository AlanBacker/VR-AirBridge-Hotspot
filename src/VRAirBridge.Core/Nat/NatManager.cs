using System.Management;
using System.Net;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Nat;

public sealed record NatInfo(string Name, string InternalPrefix, bool Active);

public enum NatProvider { None, Ics, Ours, Other, Disabled, LanOnly }

public sealed record NatStatus(NatProvider Provider, string Text, bool Ok);

/// <summary>NAT / 转发相关的系统操作（单元测试里用假实现）。</summary>
public interface INetOps
{
    List<NatInfo> GetNats();
    bool CreateNat(string name, string prefix, out string error);
    bool RemoveNat(string name);
    bool? GetForwarding(int ifIndex);
    bool SetForwarding(int ifIndex, bool enabled, out string error);
    /// <summary>按网卡名（接口别名）找 IPv4 接口索引和描述；找不到返回 false。</summary>
    bool TryFindAdapter(string name, out int ifIndex, out string description);
}

/// <summary>真正改系统的实现：WMI 读取，PowerShell New-NetNat / Remove-NetNat / Set-NetIPInterface 修改。</summary>
public sealed class SystemNetOps : INetOps
{
    public static readonly SystemNetOps Instance = new();
    public List<NatInfo> GetNats() => NatManager.GetNats();
    public bool? GetForwarding(int ifIndex) => NatManager.GetForwarding(ifIndex);
    public bool RemoveNat(string name) => NatManager.Remove(name);

    public bool TryFindAdapter(string name, out int ifIndex, out string description)
    {
        ifIndex = -1;
        description = "";
        try
        {
            var n = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n == null) return false;
            ifIndex = NetworkInfo.GetIPv4Index(n);
            description = n.Description ?? "";
            return true;
        }
        catch { return false; }
    }

    public bool CreateNat(string name, string prefix, out string error)
    {
        var r = ProcessRunner.PowerShell(
            $"try {{ New-NetNat -Name {ProcessRunner.Q(name)} -InternalIPInterfaceAddressPrefix {ProcessRunner.Q(prefix)} -ErrorAction Stop | Out-Null; 'OK' }} " +
            "catch { 'ERR:' + $_.Exception.Message; exit 1 }", 60000);
        error = r.Combined.Replace("ERR:", "").Trim();
        return r.Success && r.StdOut.Contains("OK");
    }

    public bool SetForwarding(int ifIndex, bool enabled, out string error)
    {
        var r = ProcessRunner.PowerShell(
            $"try {{ Set-NetIPInterface -InterfaceIndex {ifIndex} -AddressFamily IPv4 -Forwarding {(enabled ? "Enabled" : "Disabled")} -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 30000);
        error = r.Combined.Replace("ERR:", "").Trim();
        return r.Success;
    }
}

/// <summary>
/// 让热点上的设备能上网：ICS 正常时由 ICS 做 NAT；ICS 坏掉时创建 Windows 自带的 WinNAT（New-NetNat），
/// 并打开热点网卡和上行网卡的 IPv4 转发。所有改动记录在 config.json 的 revert 里，“还原”时撤销。
/// 退出程序时不删除 NAT（删了设备就断网了）。
/// 设置 internet.allow = false（仅局域网）时：不建 NAT、删掉本软件的 NAT、关闭热点网卡的 IPv4 转发。
/// </summary>
public sealed class NatManager
{
    private static readonly ManagementScope StdCimv2 = new(@"\\.\root\StandardCimv2");
    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly INetOps _ops;
    private DateTime _nextCreateAttempt = DateTime.MinValue;
    private DateTime _nextForwardingCheck = DateTime.MinValue;
    private DateTime _nextVirtualCleanup = DateTime.MinValue;
    private readonly HashSet<string> _skippedVirtualUplinks = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastLoggedState;
    private NatStatus _status = new(NatProvider.None, Loc.T("nat.notRunning"), false);

    public NatManager(Func<AppSettings> settings, Action saveSettings, INetOps? ops = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _ops = ops ?? SystemNetOps.Instance;
    }

    public NatStatus Status => _status;

    /// <summary>设置变了（例如切换“允许访问互联网”）：清掉所有节流计时，下一次 Reconcile 立刻生效。</summary>
    public void Invalidate()
    {
        _nextCreateAttempt = DateTime.MinValue;
        _nextForwardingCheck = DateTime.MinValue;
        _nextVirtualCleanup = DateTime.MinValue;
    }

    public static List<NatInfo> GetNats()
    {
        var list = new List<NatInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery("SELECT Name, InternalIPInterfaceAddressPrefix, Active FROM MSFT_NetNat"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    list.Add(new NatInfo(o["Name"] as string ?? "", o["InternalIPInterfaceAddressPrefix"] as string ?? "", o["Active"] is bool b && b));
        }
        catch (Exception ex) { Logger.Debug(Loc.T("nat.failedReadNetnat") + ex.Message); }
        return list;
    }

    /// <summary>prefix（例如 192.168.137.0/24）是否包含 ip。</summary>
    public static bool PrefixContains(string prefix, IPAddress ip)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || !int.TryParse(parts[1], out var len)) return false;
        if (net.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || len < 0 || len > 32) return false;
        uint mask = len == 0 ? 0 : uint.MaxValue << (32 - len);
        return (NetworkInfo.ToUInt32(net) & mask) == (NetworkInfo.ToUInt32(ip) & mask);
    }

    public static string PrefixOf(IPAddress ip, IPAddress mask) =>
        $"{NetworkInfo.NetworkAddress(ip, mask)}/{NetworkInfo.PrefixLength(mask)}";

    /// <summary>每个看门狗周期调用一次。icsServing = ICS 的 DHCP 正绑定在热点网卡上（说明 ICS 工作正常，NAT 也由它做）。</summary>
    public NatStatus Reconcile(GoAdapterInfo? go, UplinkInfo? uplink, bool icsServing, bool isAdmin)
    {
        var s = _settings();
        if (isAdmin) CleanupVirtualForwarding(go);
        if (!s.Internet.Allow)
            return ReconcileLanOnly(go, uplink, icsServing, isAdmin);
        if (go?.Ip == null || go.Mask == null || !go.HasUsableIp)
            return Set(new NatStatus(NatProvider.None, Loc.T("nat.notRunningHotspotAdapterHas"), false));
        if (string.Equals(s.Nat.Mode, "off", StringComparison.OrdinalIgnoreCase))
            return Set(new NatStatus(NatProvider.Disabled, icsServing ? Loc.T("nat.providedByWindowsIcs") : Loc.T("nat.offNatModeOff"), icsServing));
        if (icsServing)
            return Set(new NatStatus(NatProvider.Ics, Loc.T("nat.providedByWindowsIcs"), true));

        var prefix = PrefixOf(go.Ip, go.Mask);
        var nats = _ops.GetNats();
        var ours = nats.FirstOrDefault(n => string.Equals(n.Name, s.Nat.Name, StringComparison.OrdinalIgnoreCase));
        var others = nats.Where(n => n != ours).ToList();

        if (ours != null && !string.Equals(ours.InternalPrefix, prefix, StringComparison.OrdinalIgnoreCase) && isAdmin)
        {
            Logger.Info(Loc.T("nat.hotspotSubnetChangedRebuildingWinnat", prefix, ours.InternalPrefix));
            _ops.RemoveNat(s.Nat.Name);
            ours = null;
        }

        if (ours != null)
        {
            if (isAdmin) EnsureForwarding(go, uplink);
            return Set(new NatStatus(NatProvider.Ours, Loc.T("nat.winnat", ours.Name, ours.InternalPrefix), true));
        }

        var covering = others.FirstOrDefault(n => PrefixContains(n.InternalPrefix, go.Ip));
        if (covering != null)
        {
            if (isAdmin) EnsureForwarding(go, uplink);
            return Set(new NatStatus(NatProvider.Other, Loc.T("nat.providedByExistingWinnat", covering.Name, covering.InternalPrefix), true));
        }
        if (others.Count > 0)
        {
            var desc = string.Join(Loc.T("common.comma"), others.Select(n => Loc.T("common.nameParen", n.Name, n.InternalPrefix)));
            return Set(new NatStatus(NatProvider.Other,
                Loc.T("nat.pcAlreadyHasAnotherNat", desc), false));
        }

        if (!isAdmin)
            return Set(new NatStatus(NatProvider.None, Loc.T("nat.notConfiguredIcsIsnT"), false));

        if (DateTime.UtcNow < _nextCreateAttempt)
            return _status;

        Logger.Info(Loc.T("nat.icsIsnTWorkingCreating", s.Nat.Name, prefix));
        if (_ops.CreateNat(s.Nat.Name, prefix, out var err))
        {
            s.Revert.NatCreatedByUs = true;
            _saveSettings();
            Logger.Ok(Loc.T("nat.winnatCreatedUplinkDevicesHotspot", prefix));
            EnsureForwarding(go, uplink, force: true);
            return Set(new NatStatus(NatProvider.Ours, Loc.T("nat.winnat", s.Nat.Name, prefix), true));
        }
        _nextCreateAttempt = DateTime.UtcNow.AddSeconds(60);
        Logger.Error(Loc.T("nat.failedCreateWinnat") + (err.Length > 300 ? err[..300] : err));
        return Set(new NatStatus(NatProvider.None, Loc.T("nat.failedCreateWinnatSeeLog"), false));
    }

    /// <summary>
    /// 仅局域网：删除本软件建的 WinNAT，关闭热点网卡的 IPv4 转发（头显发往外网的包不会被电脑转发），
    /// 并撤销本软件以前为上网打开的上行网卡转发。电脑和头显之间的直连（串流）不受影响。
    /// </summary>
    private NatStatus ReconcileLanOnly(GoAdapterInfo? go, UplinkInfo? uplink, bool icsServing, bool isAdmin)
    {
        var s = _settings();
        var ours = _ops.GetNats().FirstOrDefault(n => string.Equals(n.Name, s.Nat.Name, StringComparison.OrdinalIgnoreCase));
        bool natLeft = false;
        if (ours != null)
        {
            if (isAdmin && _ops.RemoveNat(s.Nat.Name))
            {
                Logger.Info(Loc.T("nat.lanOnlyDeletedWinnat", s.Nat.Name));
                if (s.Revert.NatCreatedByUs) { s.Revert.NatCreatedByUs = false; _saveSettings(); }
            }
            else natLeft = true;
        }

        if (go != null && go.IfIndex > 0 && DateTime.UtcNow >= _nextForwardingCheck)
        {
            _nextForwardingCheck = DateTime.UtcNow.AddSeconds(30);
            _lanForwardingLeft = false;
            var fw = _ops.GetForwarding(go.IfIndex);
            if (fw == true)
            {
                string e1 = Loc.T("nat.administratorRightsRequired");
                if (isAdmin && _ops.SetForwarding(go.IfIndex, false, out e1))
                {
                    Logger.Info(Loc.T("nat.lanOnlyTurnedOffIpv4", go.Name));
                    RemoveFromRevertList(go.Name);
                }
                else
                {
                    // 没关掉：保留在还原列表里，“还原”时还能关
                    _lanForwardingLeft = true;
                    if (isAdmin) Logger.Warn(Loc.T("nat.failedTurnOffIpv4Forwarding", go.Name, e1));
                }
            }
            else if (fw == false) RemoveFromRevertList(go.Name);
            // 上行网卡的转发如果是本软件为上网打开的 → 一并关掉（不是本软件打开的不动）
            if (isAdmin && uplink != null && uplink.IfIndex > 0 &&
                s.Revert.ForwardingEnabledOn.Contains(uplink.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (_ops.SetForwarding(uplink.IfIndex, false, out var e2))
                {
                    Logger.Info(Loc.T("nat.lanOnlyTurnedOffIpv42", uplink.Name));
                    RemoveFromRevertList(uplink.Name);
                }
                else Logger.Warn(Loc.T("nat.failedTurnOffIpv4Forwarding", uplink.Name, e2));
            }
        }
        if (go == null) _lanForwardingLeft = false;

        if (natLeft || _lanForwardingLeft)
            return Set(new NatStatus(NatProvider.LanOnly,
                isAdmin ? Loc.T("nat.butFailedSeeLog", InternetStrings.NatLanOnly, (natLeft ? Loc.T("nat.deletingNat") : Loc.T("nat.turningOffForwarding"))) : InternetStrings.NatLanOnlyNeedAdmin, false));
        if (icsServing)
            return Set(new NatStatus(NatProvider.LanOnly, InternetStrings.NatLanOnlyIcs, false));
        return Set(new NatStatus(NatProvider.LanOnly, InternetStrings.NatLanOnly, true));
    }

    private bool _lanForwardingLeft;

    private void RemoveFromRevertList(string name)
    {
        var s = _settings();
        if (s.Revert.ForwardingEnabledOn.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) > 0)
            _saveSettings();
    }

    private NatStatus Set(NatStatus st)
    {
        _status = st;
        if (_lastLoggedState != st.Text)
        {
            _lastLoggedState = st.Text;
            Logger.Debug(Loc.T("nat.natStatus") + st.Text);
        }
        return st;
    }

    /// <summary>确保热点网卡和上行网卡开启 IPv4 转发（WinNAT 不在 Hyper-V 交换机上时需要）。每 30 秒最多检查一次。</summary>
    private void EnsureForwarding(GoAdapterInfo go, UplinkInfo? uplink, bool force = false)
    {
        if (!force && DateTime.UtcNow < _nextForwardingCheck) return;
        _nextForwardingCheck = DateTime.UtcNow.AddSeconds(30);
        var s = _settings();
        var targets = new List<(int idx, string name)> { (go.IfIndex, go.Name) };
        if (uplink != null && uplink.IfIndex > 0)
        {
            // 绝不在虚拟网卡（ZeroTier、Hyper-V、VPN……）或 Wi-Fi Direct 上开转发：它们不是真正的上行
            if (!IsVirtualUplink(uplink)) targets.Add((uplink.IfIndex, uplink.Name));
            else if (_skippedVirtualUplinks.Add(uplink.Name))
                Logger.Warn(Loc.T("nat.skipVirtualUplinkForwarding", uplink.Name, uplink.Description));
        }
        foreach (var (idx, name) in targets)
        {
            if (idx <= 0) continue;
            var fw = _ops.GetForwarding(idx);
            if (fw == true) continue;
            if (fw == null) continue;
            if (_ops.SetForwarding(idx, true, out var err))
            {
                Logger.Info(Loc.T("nat.enabledIpv4Forwarding", name));
                if (!s.Revert.ForwardingEnabledOn.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    s.Revert.ForwardingEnabledOn.Add(name);
                    _saveSettings();
                }
            }
            else Logger.Warn(Loc.T("nat.failedEnableIpv4Forwarding", name, err));
        }
    }

    /// <summary>上行是不是虚拟网卡 / Wi-Fi Direct（这种网卡上不开转发）。</summary>
    public static bool IsVirtualUplink(UplinkInfo uplink) =>
        UplinkSelector.IsVirtualOrWifiDirect(uplink.Name, uplink.Description);

    /// <summary>
    /// 以前的版本可能把 ZeroTier 之类的虚拟网卡当成上行并打开了它的 IPv4 转发（记在还原列表里）。
    /// 管理员运行时把这些转发关掉（恢复原状）并移出还原列表。热点网卡（Wi-Fi Direct）不动。每分钟最多检查一次。
    /// </summary>
    private void CleanupVirtualForwarding(GoAdapterInfo? go)
    {
        var s = _settings();
        if (s.Revert.ForwardingEnabledOn.Count == 0 || DateTime.UtcNow < _nextVirtualCleanup) return;
        _nextVirtualCleanup = DateTime.UtcNow.AddSeconds(60);
        foreach (var name in s.Revert.ForwardingEnabledOn.ToList())
        {
            if (go != null && string.Equals(name, go.Name, StringComparison.OrdinalIgnoreCase)) continue;
            bool found = _ops.TryFindAdapter(name, out var idx, out var desc);
            if (UplinkSelector.IsWifiDirectDescription(desc) || UplinkSelector.VirtualMarker(name, desc) == null) continue;
            if (!found || idx <= 0) continue;   // 网卡现在不在：留在列表里，“还原”时按名字再关
            var fw = _ops.GetForwarding(idx);
            if (fw == false) { RemoveFromRevertList(name); continue; }
            if (fw == null) continue;
            if (_ops.SetForwarding(idx, false, out var err))
            {
                Logger.Info(Loc.T("nat.turnedOffVirtualForwarding", name));
                RemoveFromRevertList(name);
            }
            else Logger.Warn(Loc.T("nat.failedTurnOffIpv4Forwarding", name, err));
        }
    }

    /// <summary>读接口的 IPv4 转发状态（WMI MSFT_NetIPInterface.Forwarding：0 关，1 开）。</summary>
    public static bool? GetForwarding(int ifIndex)
    {
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery(
                $"SELECT Forwarding FROM MSFT_NetIPInterface WHERE InterfaceIndex = {ifIndex} AND AddressFamily = 2"));
            foreach (ManagementBaseObject o in s.Get())
                using (o) return Convert.ToInt32(o["Forwarding"] ?? 0) == 1;
        }
        catch (Exception ex) { Logger.Debug(Loc.T("nat.failedReadForwardingState") + ex.Message); }
        return null;
    }

    public static bool Remove(string name)
    {
        var r = ProcessRunner.PowerShell(
            $"try {{ Get-NetNat -Name {ProcessRunner.Q(name)} -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 60000);
        if (r.Success) { Logger.Info(Loc.T("nat.deletedWinnat", name)); return true; }
        Logger.Warn(Loc.T("nat.failedDeleteWinnat", name, r.Combined));
        return false;
    }

    /// <summary>还原：删除本软件创建的 NAT，关闭本软件打开的 IPv4 转发。</summary>
    public static List<string> Revert(AppSettings s)
    {
        var report = new List<string>();
        if (s.Revert.NatCreatedByUs || GetNats().Any(n => string.Equals(n.Name, s.Nat.Name, StringComparison.OrdinalIgnoreCase)))
        {
            report.Add(Remove(s.Nat.Name) ? Loc.T("nat.deletedWinnat2", s.Nat.Name) : Loc.T("nat.failedDeleteWinnat2", s.Nat.Name));
            s.Revert.NatCreatedByUs = false;
        }
        foreach (var name in s.Revert.ForwardingEnabledOn.ToList())
        {
            var r = ProcessRunner.PowerShell(
                $"try {{ Set-NetIPInterface -InterfaceAlias {ProcessRunner.Q(name)} -AddressFamily IPv4 -Forwarding Disabled -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 30000);
            report.Add(r.Success ? Loc.T("nat.turnedOffIpv4Forwarding", name) : Loc.T("nat.forwardingCouldNotBeTurned", name));
            s.Revert.ForwardingEnabledOn.Remove(name);
        }
        return report;
    }
}

using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

public sealed record IcsRecord(string Guid, string Name, bool IsPublic, bool IsPrivate, bool AdapterExists, string Path);

/// <summary>
/// “修复网络”：处理 Windows ICS 常见的两种故障——
/// ① ICS 的 DHCP 被 Hyper-V 的 vEthernet (Default Switch) 抢走；② ICS 配置里残留指向已不存在网卡的“幽灵记录”。
/// 然后重启 ICS 服务（SharedAccess）。热点的关和开由调用方（看门狗）负责。
/// </summary>
public static class IcsRepair
{
    private static readonly ManagementScope HomeNet = new(@"\\.\root\Microsoft\HomeNet");
    private static readonly ManagementScope StdCimv2 = new(@"\\.\root\StandardCimv2");

    /// <summary>当前所有存在的网卡 GUID（含隐藏的 Wi-Fi Direct 虚拟网卡），大写不带括号。</summary>
    public static HashSet<string> LiveAdapterGuids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) set.Add(n.Id.Trim('{', '}'));
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery("SELECT InterfaceGuid FROM MSFT_NetAdapter"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    if (o["InterfaceGuid"] is string g) set.Add(g.Trim('{', '}'));
        }
        catch { }
        return set;
    }

    /// <summary>读取 ICS 记住的“公网/内网”连接（WMI root\Microsoft\HomeNet）。不需要管理员。</summary>
    public static List<IcsRecord> ReadRecords()
    {
        var res = new List<IcsRecord>();
        var live = LiveAdapterGuids();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cs = new ManagementObjectSearcher(HomeNet, new ObjectQuery("SELECT Guid, Name FROM HNet_Connection"));
            foreach (ManagementBaseObject o in cs.Get())
                using (o)
                    if (o["Guid"] is string g) names[g.Trim('{', '}')] = o["Name"] as string ?? "";
        }
        catch (Exception ex) { Logger.Debug(Loc.T("ics.failedReadHnetConnection") + ex.Message); }
        try
        {
            using var ps = new ManagementObjectSearcher(HomeNet, new ObjectQuery("SELECT * FROM HNet_ConnectionProperties"));
            foreach (ManagementObject o in ps.Get())
            {
                using (o)
                {
                    bool pub = o["IsIcsPublic"] is bool b1 && b1;
                    bool priv = o["IsIcsPrivate"] is bool b2 && b2;
                    if (!pub && !priv) continue;
                    var conn = o["Connection"]?.ToString() ?? "";
                    var m = Regex.Match(conn, "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}");
                    var g = m.Success ? m.Value.ToUpperInvariant() : conn;
                    names.TryGetValue(g, out var name);
                    res.Add(new IcsRecord(g, string.IsNullOrEmpty(name) ? g : name, pub, priv, live.Contains(g), o.Path.Path));
                }
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("ics.failedReadHnetConnectionproperties") + ex.Message); }
        return res;
    }

    /// <summary>清除 ICS 幽灵记录：先尝试把 IsIcsPublic/IsIcsPrivate 改为 false，不行再删除该记录。</summary>
    public static List<string> ClearGhostRecords()
    {
        var report = new List<string>();
        foreach (var r in ReadRecords().Where(r => !r.AdapterExists))
        {
            var role = r.IsPublic ? Loc.T("ics.publicUplink") : Loc.T("ics.privateHotspotSide");
            try
            {
                using var o = new ManagementObject(r.Path);
                o.Get();
                o["IsIcsPublic"] = false;
                o["IsIcsPrivate"] = false;
                o.Put();
                report.Add(Loc.T("ics.clearedIcsGhostEntryAdapter", role, r.Name));
            }
            catch (Exception ex1)
            {
                try
                {
                    using var o = new ManagementObject(r.Path);
                    o.Delete();
                    report.Add(Loc.T("ics.deletedIcsGhostEntry", role, r.Name));
                }
                catch (Exception ex2)
                {
                    report.Add(Loc.T("ics.failedClearIcsGhostEntry", r.Name, ex1.Message, ex2.Message));
                }
            }
        }
        if (report.Count == 0) report.Add(Loc.T("ics.noIcsGhostEntriesFound"));
        foreach (var line in report) Logger.Info(Loc.T("ics.repairNetwork") + line);
        return report;
    }

    /// <summary>ICS 的 DHCP（UDP 67）当前绑定在哪些地址上，附带网卡名。</summary>
    public static List<(IPAddress Ip, string? Adapter, string? Description)> DhcpBindings()
    {
        var res = new List<(IPAddress, string?, string?)>();
        int self = Environment.ProcessId;
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { nics = Array.Empty<NetworkInterface>(); }
        foreach (var l in IpHelper.GetUdpListeners().Where(l => l.Port == 67 && l.Pid != self))
        {
            var nic = nics.FirstOrDefault(n =>
            {
                try { return n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(l.Address)); } catch { return false; }
            });
            res.Add((l.Address, nic?.Name, nic?.Description));
        }
        return res;
    }

    public static bool IsHyperVSwitch(string? name, string? desc) =>
        (name ?? "").Contains("Default Switch", StringComparison.OrdinalIgnoreCase) ||
        (desc ?? "").Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前启用着的 Hyper-V Default Switch 网卡名（没有则 null）。</summary>
    public static string? FindEnabledDefaultSwitch()
    {
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery("SELECT Name, InterfaceDescription, State, InterfaceAdminStatus FROM MSFT_NetAdapter"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                {
                    var name = o["Name"] as string ?? "";
                    if (name.Contains("Default Switch", StringComparison.OrdinalIgnoreCase) && DongleInfo.EffectiveState(o) != 3) return name;
                }
        }
        catch { }
        return null;
    }

    public static bool SetAdapterEnabled(string name, bool enable)
    {
        var verb = enable ? "Enable-NetAdapter" : "Disable-NetAdapter";
        var r = ProcessRunner.PowerShell($"try {{ {verb} -Name {ProcessRunner.Q(name)} -Confirm:$false -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 60000);
        if (!r.Success) Logger.Warn(Loc.T("ics.failed", (enable ? Loc.T("ics.enable") : Loc.T("ics.disable")), name, r.Combined.Replace("ERR:", "")));
        return r.Success;
    }

    /// <summary>
    /// 修复步骤 2–4（关热点之后、开热点之前调用）：处理 Default Switch、清幽灵记录、重启 SharedAccess。
    /// confirm 用来询问用户是否禁用 Default Switch。
    /// </summary>
    public static async Task<List<string>> RepairIcsAsync(AppSettings s, Action save, Func<string, bool> confirm)
    {
        var report = new List<string>();
        void Add(string t, LogLevel l = LogLevel.Info) { report.Add(t); Logger.Write(l, Loc.T("ics.repairNetwork") + t); }

        // 2. Hyper-V Default Switch 抢占 ICS 的 DHCP
        var bindings = DhcpBindings();
        foreach (var b in bindings) Add(Loc.T("ics.icsDhcpCurrentlyBound", b.Ip, b.Adapter ?? "?"));
        var ds = FindEnabledDefaultSwitch();
        bool dsHoldsDhcp = bindings.Any(b => IsHyperVSwitch(b.Adapter, b.Description));
        if (ds != null)
        {
            var q = dsHoldsDhcp
                ? Loc.T("ics.dhcpWindowsIcsTakenBy", ds, ds)
                : Loc.T("ics.hyperVDefaultSwitchEnabled", ds, ds);
            if (confirm(q))
            {
                if (SetAdapterEnabled(ds, false))
                {
                    if (!s.Revert.DisabledAdapters.Contains(ds, StringComparer.OrdinalIgnoreCase)) { s.Revert.DisabledAdapters.Add(ds); save(); }
                    Add(Loc.T("ics.disabledCanBeReEnabled", ds), LogLevel.Ok);
                }
                else Add(Loc.T("ics.failedDisable", ds), LogLevel.Warn);
            }
            else Add(Loc.T("ics.leftUnchanged", ds));
        }
        else Add(Loc.T("ics.hyperVDefaultSwitchDoesn"));

        // 3. ICS 幽灵记录
        foreach (var r in ReadRecords())
            Add(Loc.T("ics.icsEntry", (r.IsPublic ? Loc.T("ics.public") : Loc.T("ics.private")), r.Name, (r.AdapterExists ? "" : Loc.T("ics.adapterNoLongerExists"))));
        report.AddRange(ClearGhostRecords());

        // 4. 重启 ICS 服务
        Add(Loc.T("ics.restartingIcsServiceSharedaccess"));
        bool stopped = await ServiceUtil.StopReliablyAsync("SharedAccess");
        Add(stopped ? Loc.T("ics.icsServiceStopped") : Loc.T("ics.icsServiceWonTStop"), stopped ? LogLevel.Info : LogLevel.Warn);
        await Task.Delay(1500);
        bool started = await ServiceUtil.StartAsync("SharedAccess");
        Add(started ? Loc.T("ics.icsServiceRestarted") : Loc.T("ics.icsServiceIsnTRunning"), started ? LogLevel.Ok : LogLevel.Info);
        return report;
    }
}

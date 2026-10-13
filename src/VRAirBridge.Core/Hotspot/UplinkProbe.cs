using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Util;
using Windows.Networking.Connectivity;

namespace VRAirBridge.Core.Hotspot;

/// <summary>
/// 收集上行候选：Windows 连接配置文件（GetConnectionProfiles）按网卡 GUID 关联 NetworkInterface，
/// 再加上 IPv4 默认路由的有效跃点数。只读，不改系统。
/// </summary>
internal static class UplinkProbe
{
    internal sealed record Entry(UplinkCandidate Candidate, ConnectionProfile Profile);

    internal sealed record Result(IReadOnlyList<Entry> Entries, string? WindowsDefaultAdapterId);

    private const uint IanaEthernet = 6, IanaWifi = 71, IanaLoopback = 24;

    public static Result Probe(string? dongleGuid, string? dongleMac)
    {
        ConnectionProfile? win = null;
        try { win = NetworkInformation.GetInternetConnectionProfile(); } catch { }
        var profiles = new List<ConnectionProfile>();
        if (win != null) profiles.Add(win);
        try { profiles.AddRange(NetworkInformation.GetConnectionProfiles()); } catch { }

        var nics = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces()) nics[Norm(n.Id)] = n;
        }
        catch { }
        Dictionary<int, int> metrics;
        try { metrics = IpHelper.GetDefaultRouteMetrics(); } catch { metrics = new(); }

        string? winId = win == null ? null : AdapterIdOf(win);
        var byAdapter = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var p in profiles)
        {
            var id = AdapterIdOf(p);
            if (id == null) continue;
            var c = Build(p, id, nics, metrics, dongleGuid, dongleMac);
            if (byAdapter.TryGetValue(id, out var old))
            {
                // 同一块网卡有多个配置文件（例如以前连过的 Wi-Fi）：取连通性最好的那个
                if (UplinkSelector.ConnectivityRank(c.Connectivity) > UplinkSelector.ConnectivityRank(old.Candidate.Connectivity))
                    byAdapter[id] = new Entry(c, p);
                continue;
            }
            byAdapter[id] = new Entry(c, p);
            order.Add(id);
        }
        return new Result(order.Select(i => byAdapter[i]).ToList(), winId);
    }

    private static string Norm(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    private static string? AdapterIdOf(ConnectionProfile p)
    {
        try
        {
            var a = p.NetworkAdapter;
            return a == null ? null : Norm(a.NetworkAdapterId.ToString());
        }
        catch { return null; }
    }

    private static UplinkCandidate Build(ConnectionProfile p, string id, Dictionary<string, NetworkInterface> nics,
        Dictionary<int, int> metrics, string? dongleGuid, string? dongleMac)
    {
        var level = NetworkConnectivityLevel.None;
        try { level = p.GetNetworkConnectivityLevel(); } catch { }
        string profileName = "";
        try { profileName = p.ProfileName ?? ""; } catch { }
        bool isWlanProfile = false;
        try { isWlanProfile = p.IsWlanConnectionProfile; } catch { }
        uint iana = 0;
        try { iana = p.NetworkAdapter?.IanaInterfaceType ?? 0; } catch { }

        nics.TryGetValue(id, out var ni);
        string name = "", desc = "", mac = "";
        bool up = level != NetworkConnectivityLevel.None, hasGw = false, hasIp = false;
        int ifIndex = -1;
        NetworkInterfaceType type = NetworkInterfaceType.Unknown;
        if (ni != null)
        {
            try { name = ni.Name ?? ""; desc = ni.Description ?? ""; type = ni.NetworkInterfaceType; } catch { }
            try { up = ni.OperationalStatus == OperationalStatus.Up; } catch { }
            try { mac = MacUtil.Format(ni.GetPhysicalAddress()); } catch { }
            try
            {
                var props = ni.GetIPProperties();
                hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                hasIp = props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !NetworkInfo.IsApipa(a.Address));
                ifIndex = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch { }
        }
        int? metric = ifIndex > 0 && metrics.TryGetValue(ifIndex, out var m) ? m : null;
        bool wifi = isWlanProfile || iana == IanaWifi || type == NetworkInterfaceType.Wireless80211;
        bool eth = !wifi && (iana == IanaEthernet || type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit);
        bool loop = iana == IanaLoopback || type == NetworkInterfaceType.Loopback;
        bool wfd = desc.StartsWith(NetworkInfo.WifiDirectPrefix, StringComparison.OrdinalIgnoreCase) || UplinkSelector.IsWifiDirectDescription(desc);
        bool dongle = UplinkSelector.SameAdapter(id, dongleGuid)
                      || (!wfd && MacUtil.SameRadio(mac, dongleMac))
                      || desc.Contains(DongleInfo.DescriptionKeyword, StringComparison.OrdinalIgnoreCase);
        return new UplinkCandidate(profileName, name, desc, id, (UplinkConnectivity)(int)level, wifi, eth, hasGw, metric, hasIp,
            IsDongle: dongle, IsWifiDirect: wfd, IsLoopback: loop, IsUp: up);
    }
}

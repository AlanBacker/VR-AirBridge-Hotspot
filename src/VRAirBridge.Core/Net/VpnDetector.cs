using System.Net.NetworkInformation;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.Net;

/// <summary>One network adapter as the VPN check sees it. IfIndex = IPv4 interface index (-1 = no IPv4).</summary>
public sealed record NetAdapterSample(string Name, string Description, NetworkInterfaceType Type, bool IsUp, int IfIndex);

/// <summary>A full-tunnel VPN that is connected now. Marker = what identified it ("WireGuard", "TAP", "ZeroTier", "PPP"...).</summary>
public sealed record VpnAdapter(string Name, string Description, string Marker)
{
    /// <summary>"WindscribeWireguard (WireGuard Tunnel)": the adapter name, plus the description when it says more.</summary>
    public string Display =>
        Description.Length == 0 || Description.Equals(Name, StringComparison.OrdinalIgnoreCase) ? Name : Loc.T("common.nameParen", Name, Description);
}

/// <summary>
/// Finds the full-tunnel VPNs that are connected right now (pure, unit-tested, apart from <see cref="Detect"/>).
/// The hotspot shares the uplink through Windows ICS, which turns on IPv4 forwarding on the uplink (Wi-Fi); a full-tunnel
/// VPN such as WireGuard (Windscribe) may then lose its connection.
/// - Only adapters that carry the default route Windows really uses count (0.0.0.0/0 with the lowest metric, or the
///   0.0.0.0/1 + 128.0.0.0/1 pair). Game accelerators (UU in TUN mode) and other split tunnels only route some
///   addresses through their adapter and work fine next to the hotspot. So does ZeroTier's 0.0.0.0/0 route with metric
///   9999, which never wins.
/// - Of those, VPN adapters (WireGuard, Wintun, TAP, TUN, OpenVPN, Windscribe...) and overlay networks / proxies in global
///   mode (ZeroTier, Tailscale, Clash...) count.
/// - A PPP connection (the VPN built into Windows) counts when it carries the winning default route while a physical
///   adapter has one too. PPPoE broadband is the only way out, so it does not count.
/// - Hypervisor switches, system tunnels (Teredo, IP-HTTPS...), Bluetooth, Wi-Fi Direct and loopback never count.
/// - Neither do entries without IPv4 (ICS only breaks IPv4): Windows also lists every NDIS filter of an adapter as an "up"
///   entry without IPv4, e.g. "WLAN-Kaspersky Lab NDIS 6 Filter-0000", and a VPN client's filter on the Wi-Fi would match.
/// </summary>
public static class VpnDetector
{
    /// <summary>Name or description contains one of these (case-insensitive): a VPN adapter.</summary>
    public static readonly IReadOnlyList<string> VpnMarkers = new[]
    {
        "WireGuard", "Wintun", "Windscribe", "OpenVPN", "TAP-Windows", "TAP-Win32", "NordLynx", "Mullvad", "Surfshark",
        "AnyConnect", "GlobalProtect", "PANGP", "Fortinet", "FortiClient", "SoftEther", "Cloudflare WARP",
        "Private Internet Access", "Hotspot Shield", "CyberGhost", "VPN",
    };

    /// <summary>Whole words only: "Clash TUN" counts, "Teredo Tunneling" does not.</summary>
    public static readonly IReadOnlyList<string> VpnWords = new[] { "TAP", "TUN" };

    /// <summary>Overlay networks / proxies (exit node / global mode).</summary>
    public static readonly IReadOnlyList<string> OverlayMarkers = new[]
    {
        "ZeroTier", "Tailscale", "Hamachi", "Radmin", "Clash", "Mihomo", "sing-box", "v2ray", "Xray",
    };

    /// <summary>Never a VPN, whatever routes they have (a Hyper-V external switch carries the real default route).</summary>
    public static readonly IReadOnlyList<string> NeverMarkers = new[]
    {
        "Hyper-V", "vEthernet", "vSwitch", "VMware", "VirtualBox", "Npcap", "Bluetooth", "Kernel Debug", "Loopback",
        "Teredo", "6to4", "IP-HTTPS", "ISATAP", "Pseudo-Interface", "WAN Miniport",
    };

    private static readonly NetworkInterfaceType[] PhysicalTypes =
    {
        NetworkInterfaceType.Ethernet, NetworkInterfaceType.Wireless80211, NetworkInterfaceType.GigabitEthernet,
        NetworkInterfaceType.FastEthernetT, NetworkInterfaceType.FastEthernetFx, NetworkInterfaceType.Ethernet3Megabit,
    };

    private const uint HalfMask = 0x80000000, UpperHalf = 0x80000000;

    /// <summary>The connected VPNs on this PC. Never throws (empty when the adapters or routes can't be read).</summary>
    public static List<VpnAdapter> Detect()
    {
        try { return Find(SampleAdapters(), IpHelper.GetIPv4Routes()); }
        catch (Exception ex)
        {
            Logger.Debug("VPN check: " + ex.Message);
            return new List<VpnAdapter>();
        }
    }

    public static List<NetAdapterSample> SampleAdapters()
    {
        var list = new List<NetAdapterSample>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            NetworkInterfaceType type;
            try { type = n.NetworkInterfaceType; } catch { type = NetworkInterfaceType.Unknown; }
            list.Add(new NetAdapterSample(n.Name ?? "", n.Description ?? "", type, n.OperationalStatus == OperationalStatus.Up, NetworkInfo.GetIPv4Index(n)));
        }
        return list;
    }

    /// <summary>"WindscribeWireguard, Ethernet 2": the adapter names for messages.</summary>
    public static string Names(IEnumerable<VpnAdapter> vpns) =>
        string.Join(Loc.T("common.enumSep"), vpns.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase));

    public static List<VpnAdapter> Find(IReadOnlyList<NetAdapterSample> adapters, IReadOnlyList<RouteRow> routes)
    {
        var winning = WinningDefaultRoutes(routes);
        var anyDefault = new HashSet<int>(IpHelper.DefaultRouteMetrics(routes).Keys);
        anyDefault.UnionWith(SplitDefaultRoutes(routes));
        var res = new List<VpnAdapter>();
        foreach (var a in adapters)
        {
            var marker = Classify(a, winning, physicalHasDefault: adapters.Any(o =>
                o.IfIndex != a.IfIndex && o.IsUp && IsPhysical(o) && anyDefault.Contains(o.IfIndex)));
            if (marker != null) res.Add(new VpnAdapter(a.Name, a.Description, marker));
        }
        return res;
    }

    /// <summary>
    /// Why this adapter is a connected full-tunnel VPN (the marker), or null.
    /// winningDefault = interfaces whose default route Windows uses; physicalHasDefault = another, physical adapter has a default route.
    /// </summary>
    public static string? Classify(NetAdapterSample a, IReadOnlySet<int> winningDefault, bool physicalHasDefault)
    {
        if (!a.IsUp || a.IfIndex <= 0 || a.Type == NetworkInterfaceType.Loopback) return null;
        if (UplinkSelector.IsWifiDirectDescription(a.Description) || Marker(a, NeverMarkers) != null) return null;
        if (!winningDefault.Contains(a.IfIndex)) return null;   // split tunnel (UU TUN mode...) or idle
        var marker = Marker(a, OverlayMarkers) ?? Marker(a, VpnMarkers) ?? Word(a, VpnWords);
        if (marker != null) return marker;
        if (a.Type == NetworkInterfaceType.Ppp && physicalHasDefault) return "PPP";
        return null;
    }

    /// <summary>An up Ethernet / Wi-Fi adapter that is not virtual (no VPN / overlay / hypervisor marker, not Wi-Fi Direct).</summary>
    public static bool IsPhysical(NetAdapterSample a) =>
        PhysicalTypes.Contains(a.Type) && !UplinkSelector.IsWifiDirectDescription(a.Description) &&
        Marker(a, NeverMarkers) == null && Marker(a, OverlayMarkers) == null && Marker(a, VpnMarkers) == null && Word(a, VpnWords) == null &&
        UplinkSelector.VirtualMarker(a.Name, a.Description) == null;

    /// <summary>
    /// Interfaces that carry the IPv4 default route Windows really uses: the 0.0.0.0/0 route(s) with the lowest metric,
    /// plus interfaces with the 0.0.0.0/1 + 128.0.0.0/1 pair (longer prefixes than /0, so they always win; OpenVPN and others use it).
    /// </summary>
    public static HashSet<int> WinningDefaultRoutes(IReadOnlyList<RouteRow> routes)
    {
        var res = SplitDefaultRoutes(routes);
        var metrics = IpHelper.DefaultRouteMetrics(routes);
        if (metrics.Count > 0)
        {
            var min = metrics.Values.Min();
            foreach (var (ifIndex, m) in metrics)
                if (m == min) res.Add(ifIndex);
        }
        return res;
    }

    private static HashSet<int> SplitDefaultRoutes(IReadOnlyList<RouteRow> routes)
    {
        var lower = routes.Where(r => r.Mask == HalfMask && r.Destination == 0).Select(r => r.IfIndex).ToHashSet();
        var upper = routes.Where(r => r.Mask == HalfMask && r.Destination == UpperHalf).Select(r => r.IfIndex).ToHashSet();
        lower.IntersectWith(upper);
        lower.RemoveWhere(i => i <= 0);
        return lower;
    }

    private static string? Marker(NetAdapterSample a, IReadOnlyList<string> markers)
    {
        foreach (var m in markers)
            if (a.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(m, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    private static string? Word(NetAdapterSample a, IReadOnlyList<string> words)
    {
        static IEnumerable<string> Tokens(string s) => s.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var tokens = Tokens(a.Name).Concat(Tokens(a.Description)).ToList();
        return words.FirstOrDefault(w => tokens.Any(t => t.Equals(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly char[] Separators = { ' ', '-', '_', '(', ')', '[', ']', '#', '.', ',', '/', ':' };
}

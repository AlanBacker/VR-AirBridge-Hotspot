using System.Net.NetworkInformation;
using VRAirBridge.Core;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.SystemSetup;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>Adapters seen on the owner's PC (WLAN uplink, D-Link dongle, ZeroTier with its never-winning default route) plus VPNs.</summary>
internal static class Adapters
{
    public static NetAdapterSample Wlan(bool up = true) =>
        new("WLAN", "Realtek 8852CE WiFi 6E PCI-E NIC", NetworkInterfaceType.Wireless80211, up, 12);

    public static NetAdapterSample Dongle(bool up = true) =>
        new("WLAN 2", "D-Link DWA-F18 VR Air Bridge", NetworkInterfaceType.Wireless80211, up, 18);

    public static NetAdapterSample Ethernet(bool up = true, int ifIndex = 7) =>
        new("Ethernet", "Realtek Gaming 2.5GbE Family Controller", NetworkInterfaceType.Ethernet, up, ifIndex);

    public static NetAdapterSample ZeroTier() =>
        new("ZeroTier One [68bea79acf8db002]", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet, true, 20);

    public static NetAdapterSample WifiDirect() =>
        new("Local Area Connection* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2", NetworkInterfaceType.Wireless80211, true, 25);

    public static NetAdapterSample Windscribe(bool up = true) =>
        new("WindscribeWireguard", "WireGuard Tunnel", NetworkInterfaceType.Unknown, up, 30);

    public static NetAdapterSample Loopback() =>
        new("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, true, 1);

    public static NetAdapterSample Teredo() =>
        new("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, true, 14);

    public static NetAdapterSample HyperV() =>
        new("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true, 40);

    /// <summary>A game accelerator's TUN adapter (UU in router / TUN mode; the name is illustrative).</summary>
    public static NetAdapterSample AcceleratorTun() =>
        new("UU", "UU TUN Adapter", NetworkInterfaceType.Unknown, true, 55);

    /// <summary>Routes on the owner's PC: WLAN wins (75), ZeroTier's 0.0.0.0/0 never does (10034).</summary>
    public static List<RouteRow> UserRoutes() => new()
    {
        RouteRow.Of("0.0.0.0/0", 12, 75),
        RouteRow.Of("192.168.8.0/24", 12, 331),
        RouteRow.Of("0.0.0.0/0", 20, 10034),
        RouteRow.Of("10.147.17.0/24", 20, 10034),
        RouteRow.Of("127.0.0.0/8", 1, 331),
    };
}

/// <summary>
/// VPN check before / while the hotspot runs: flag full-tunnel VPNs (they carry the winning default route), never split
/// tunnels such as a game accelerator in TUN mode, ZeroTier's idle default route or system adapters.
/// </summary>
public class VpnDetectorTests
{
    private static readonly IReadOnlySet<int> NoRoutes = new HashSet<int>();

    [Fact]
    public void Find_UserPcWithoutVpn_FindsNothing()
    {
        var adapters = new[] { Adapters.Wlan(), Adapters.Dongle(), Adapters.ZeroTier(), Adapters.WifiDirect(), Adapters.Loopback(), Adapters.Teredo(), Adapters.HyperV() };
        Assert.Empty(VpnDetector.Find(adapters, Adapters.UserRoutes()));
    }

    [Fact]
    public void Find_WindscribeConnected_IsFlagged()
    {
        var adapters = new[] { Adapters.Wlan(), Adapters.ZeroTier(), Adapters.Windscribe() };
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("0.0.0.0/1", 30, 5));
        routes.Add(RouteRow.Of("128.0.0.0/1", 30, 5));
        var vpn = Assert.Single(VpnDetector.Find(adapters, routes));
        Assert.Equal("WindscribeWireguard", vpn.Name);
        Assert.Equal("WireGuard", vpn.Marker);
    }

    [Fact]
    public void Find_WindscribeFullTunnelOverDefaultRoute_IsFlagged()
    {
        // WireGuard for Windows style: its own 0.0.0.0/0 with metric 0 beats WLAN's 75
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("0.0.0.0/0", 30, 0));
        var vpn = Assert.Single(VpnDetector.Find(new[] { Adapters.Wlan(), Adapters.Windscribe() }, routes));
        Assert.Equal("WireGuard", vpn.Marker);
    }

    [Fact]
    public void Find_WindscribeDisconnected_IsNotFlagged()
    {
        var adapters = new[] { Adapters.Wlan(), Adapters.Windscribe(up: false) };
        Assert.Empty(VpnDetector.Find(adapters, Adapters.UserRoutes()));
    }

    [Fact]
    public void Find_GameAcceleratorTun_IsNotFlagged()
    {
        // UU in TUN mode works next to the hotspot: it only routes the game servers through its adapter
        var adapters = new[] { Adapters.Wlan(), Adapters.Dongle(), Adapters.ZeroTier(), Adapters.AcceleratorTun() };
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("203.0.113.0/24", 55, 5));
        routes.Add(RouteRow.Of("198.51.100.17/32", 55, 5));
        Assert.Empty(VpnDetector.Find(adapters, routes));
    }

    [Fact]
    public void Find_SplitTunnelVpn_IsNotFlagged()
    {
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("10.8.0.0/16", 30, 5));
        Assert.Empty(VpnDetector.Find(new[] { Adapters.Wlan(), Adapters.Windscribe() }, routes));
    }

    [Theory]
    [InlineData("WindscribeWireguard", "WireGuard Tunnel", "WireGuard")]
    [InlineData("Ethernet 3", "Wintun Userspace Tunnel", "Wintun")]
    [InlineData("Ethernet 4", "TAP-Windows Adapter V9", "TAP-Windows")]
    [InlineData("Ethernet 5", "TAP-Win32 Adapter OAS", "TAP-Win32")]
    [InlineData("NordLynx", "NordLynx Tunnel", "NordLynx")]
    [InlineData("Ethernet 6", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", "AnyConnect")]
    [InlineData("Ethernet 7", "PANGP Virtual Ethernet Adapter Secure", "PANGP")]
    [InlineData("Ethernet 8", "TAP Adapter", "TAP")]
    [InlineData("Meta", "Meta TUN Device", "TUN")]
    [InlineData("Office VPN", "Office VPN", "VPN")]
    [InlineData("UU", "UU TUN Adapter", "TUN")]
    public void Classify_VpnAdapter_CountsOnlyWithTheWinningDefaultRoute(string name, string description, string marker)
    {
        var a = new NetAdapterSample(name, description, NetworkInterfaceType.Unknown, true, 50);
        Assert.Null(VpnDetector.Classify(a, NoRoutes, physicalHasDefault: true));
        Assert.Null(VpnDetector.Classify(a, new HashSet<int> { 12 }, physicalHasDefault: true));
        Assert.Equal(marker, VpnDetector.Classify(a, new HashSet<int> { 50 }, physicalHasDefault: true));
        Assert.Equal(marker, VpnDetector.Classify(a, new HashSet<int> { 12, 50 }, physicalHasDefault: true));
    }

    [Fact]
    public void Classify_DownVpnAdapter_DoesNotCount()
    {
        var a = new NetAdapterSample("Ethernet 4", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, false, 50);
        Assert.Null(VpnDetector.Classify(a, new HashSet<int> { 50 }, physicalHasDefault: true));
    }

    [Theory]
    [InlineData("WLAN-Fortinet NDIS 6.3 Packet Filter Driver-0000", "Realtek 8852CE WiFi 6E PCI-E NIC-Fortinet NDIS 6.3 Packet Filter Driver-0000")]
    [InlineData("WLAN-Windscribe VPN Filter-0000", "Realtek 8852CE WiFi 6E PCI-E NIC-Windscribe VPN Filter-0000")]
    [InlineData("WindscribeWireguard-QoS Packet Scheduler-0000", "WireGuard Tunnel-QoS Packet Scheduler-0000")]
    public void Classify_NdisFilterEntriesWithoutIPv4_NeverCount(string name, string description)
    {
        // Windows lists each filter of an adapter as its own "up" entry, without IPv4
        var a = new NetAdapterSample(name, description, NetworkInterfaceType.Wireless80211, true, -1);
        Assert.Null(VpnDetector.Classify(a, new HashSet<int> { 12, 30 }, physicalHasDefault: true));
    }

    [Fact]
    public void Find_WindscribeConnected_ListedOnceDespiteItsFilterEntries()
    {
        var adapters = new[]
        {
            Adapters.Wlan(), Adapters.Windscribe(),
            new NetAdapterSample("WindscribeWireguard-WFP Native MAC Layer LightWeight Filter-0000", "WireGuard Tunnel-WFP Native MAC Layer LightWeight Filter-0000", NetworkInterfaceType.Unknown, true, -1),
            new NetAdapterSample("WLAN-Npcap Packet Driver (NPCAP)-0000", "Realtek 8852CE WiFi 6E PCI-E NIC-Npcap Packet Driver (NPCAP)-0000", NetworkInterfaceType.Wireless80211, true, -1),
        };
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("0.0.0.0/0", 30, 0));
        Assert.Equal(new[] { "WindscribeWireguard" }, VpnDetector.Find(adapters, routes).Select(v => v.Name));
    }

    [Theory]
    [InlineData("Teredo Tunneling Pseudo-Interface")]
    [InlineData("Microsoft IP-HTTPS Platform Adapter")]
    [InlineData("Some Tunnel Adapter")]
    [InlineData("Taptic Engine Driver")]
    public void Classify_TapAndTunMatchWholeWordsOnly(string description)
    {
        var a = new NetAdapterSample("Adapter", description, NetworkInterfaceType.Tunnel, true, 50);
        Assert.Null(VpnDetector.Classify(a, new HashSet<int> { 50 }, physicalHasDefault: true));
    }

    [Fact]
    public void Classify_ZeroTier_OnlyWhenItCarriesTheWinningDefaultRoute()
    {
        var zt = Adapters.ZeroTier();
        Assert.Null(VpnDetector.Classify(zt, VpnDetector.WinningDefaultRoutes(Adapters.UserRoutes()), physicalHasDefault: true));

        var exitNode = Adapters.UserRoutes();
        exitNode.Add(RouteRow.Of("0.0.0.0/1", 20, 10034));
        exitNode.Add(RouteRow.Of("128.0.0.0/1", 20, 10034));
        Assert.Equal("ZeroTier", VpnDetector.Classify(zt, VpnDetector.WinningDefaultRoutes(exitNode), physicalHasDefault: true));
    }

    [Theory]
    [InlineData("Tailscale", "Tailscale Tunnel", "Tailscale")]
    [InlineData("Clash", "Meta Tunnel", "Clash")]
    [InlineData("Ethernet 9", "Hamachi Network Interface", "Hamachi")]
    public void Classify_Overlay_CountsOnlyWhenWinning(string name, string description, string marker)
    {
        var a = new NetAdapterSample(name, description, NetworkInterfaceType.Unknown, true, 60);
        Assert.Null(VpnDetector.Classify(a, NoRoutes, physicalHasDefault: true));
        Assert.Equal(marker, VpnDetector.Classify(a, new HashSet<int> { 60 }, physicalHasDefault: true));
    }

    [Fact]
    public void Classify_SystemAndHypervisorAdapters_NeverCount_EvenWithTheWinningRoute()
    {
        var winning = new HashSet<int> { 1, 14, 25, 40, 41, 42 };
        Assert.Null(VpnDetector.Classify(Adapters.Loopback(), winning, true));
        Assert.Null(VpnDetector.Classify(Adapters.Teredo(), winning, true));
        Assert.Null(VpnDetector.Classify(Adapters.WifiDirect(), winning, true));
        Assert.Null(VpnDetector.Classify(Adapters.HyperV(), winning, true));
        Assert.Null(VpnDetector.Classify(new NetAdapterSample("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", NetworkInterfaceType.Ethernet, true, 41), winning, true));
        Assert.Null(VpnDetector.Classify(new NetAdapterSample("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet, true, 42), winning, true));
    }

    [Fact]
    public void Classify_PhysicalUplink_IsNeverAVpn()
    {
        var winning = new HashSet<int> { 12, 18, 7 };
        Assert.Null(VpnDetector.Classify(Adapters.Wlan(), winning, true));
        Assert.Null(VpnDetector.Classify(Adapters.Dongle(), winning, true));
        Assert.Null(VpnDetector.Classify(Adapters.Ethernet(), winning, true));
    }

    [Fact]
    public void Classify_Ppp_CountsOnlyAsAVpnOverAnotherUplink()
    {
        var ppp = new NetAdapterSample("Office", "Office", NetworkInterfaceType.Ppp, true, 70);
        var winning = new HashSet<int> { 70 };
        Assert.Equal("PPP", VpnDetector.Classify(ppp, winning, physicalHasDefault: true));
        Assert.Null(VpnDetector.Classify(ppp, winning, physicalHasDefault: false));   // PPPoE broadband
        Assert.Null(VpnDetector.Classify(ppp, NoRoutes, physicalHasDefault: true));   // split tunnel
    }

    [Fact]
    public void Find_PppoeBroadband_IsNotAVpn()
    {
        var adapters = new[] { Adapters.Ethernet(), new NetAdapterSample("Broadband", "Broadband", NetworkInterfaceType.Ppp, true, 70) };
        var routes = new List<RouteRow> { RouteRow.Of("0.0.0.0/0", 70, 26), RouteRow.Of("192.168.1.0/24", 7, 281) };
        Assert.Empty(VpnDetector.Find(adapters, routes));
    }

    [Fact]
    public void Find_WindowsVpnOverWlan_IsFlagged()
    {
        var adapters = new[] { Adapters.Wlan(), new NetAdapterSample("Office", "Office", NetworkInterfaceType.Ppp, true, 70) };
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("0.0.0.0/0", 70, 26));
        var vpn = Assert.Single(VpnDetector.Find(adapters, routes));
        Assert.Equal("PPP", vpn.Marker);
    }

    [Fact]
    public void WinningDefaultRoutes_LowestMetricPlusSplitPairs()
    {
        var routes = Adapters.UserRoutes();
        Assert.Equal(new[] { 12 }, VpnDetector.WinningDefaultRoutes(routes).OrderBy(i => i));

        routes.Add(RouteRow.Of("0.0.0.0/1", 30, 5));
        routes.Add(RouteRow.Of("128.0.0.0/1", 30, 5));
        routes.Add(RouteRow.Of("0.0.0.0/1", 31, 5));          // half a pair does not win
        routes.Add(RouteRow.Of("0.0.0.0/0", 7, 75));          // tie with WLAN
        Assert.Equal(new[] { 7, 12, 30 }, VpnDetector.WinningDefaultRoutes(routes).OrderBy(i => i));

        Assert.Empty(VpnDetector.WinningDefaultRoutes(new List<RouteRow>()));
    }

    [Fact]
    public void DefaultRouteMetrics_LowestMetricPerInterface()
    {
        var routes = Adapters.UserRoutes();
        routes.Add(RouteRow.Of("0.0.0.0/0", 12, 50));
        var m = IpHelper.DefaultRouteMetrics(routes);
        Assert.Equal(2, m.Count);
        Assert.Equal(50, m[12]);
        Assert.Equal(10034, m[20]);
    }

    [Fact]
    public void RouteRow_Of_ParsesPrefixInHostOrder()
    {
        Assert.Equal(new RouteRow(0, 0, 1, 2), RouteRow.Of("0.0.0.0/0", 1, 2));
        Assert.Equal(new RouteRow(0x80000000, 0x80000000, 1, 2), RouteRow.Of("128.0.0.0/1", 1, 2));
        Assert.Equal(new RouteRow(0xC0A80800, 0xFFFFFF00, 1, 2), RouteRow.Of("192.168.8.0/24", 1, 2));
        Assert.Equal(new RouteRow(0xC0A80801, 0xFFFFFFFF, 1, 2), RouteRow.Of("192.168.8.1/32", 1, 2));
    }

    [Fact]
    public void IsPhysical_OnlyRealEthernetAndWifi()
    {
        Assert.True(VpnDetector.IsPhysical(Adapters.Wlan()));
        Assert.True(VpnDetector.IsPhysical(Adapters.Dongle()));
        Assert.True(VpnDetector.IsPhysical(Adapters.Ethernet()));
        Assert.False(VpnDetector.IsPhysical(Adapters.ZeroTier()));
        Assert.False(VpnDetector.IsPhysical(Adapters.WifiDirect()));
        Assert.False(VpnDetector.IsPhysical(Adapters.HyperV()));
        Assert.False(VpnDetector.IsPhysical(Adapters.Windscribe()));
        Assert.False(VpnDetector.IsPhysical(new NetAdapterSample("Ethernet 4", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, true, 50)));
    }

    [Fact]
    public void Display_AndNames()
    {
        var ws = new VpnAdapter("WindscribeWireguard", "WireGuard Tunnel", "WireGuard");
        var office = new VpnAdapter("Office", "Office", "PPP");
        Assert.Equal("WindscribeWireguard（WireGuard Tunnel）", ws.Display);
        Assert.Equal("Office", office.Display);
        Assert.Equal("WindscribeWireguard、Office", VpnDetector.Names(new[] { ws, office, ws }));
    }

    [Fact]
    public void Messages_SayAFullTunnelVpnMayLoseItsConnection()
    {
        const string phrase = "热点和全局 VPN 同时使用时，VPN 可能会断网";
        Assert.Contains(phrase, Loc.T("vpn.confirm", "• WindscribeWireguard"));
        Assert.Contains(phrase, Loc.T("vpn.hint", "WindscribeWireguard"));
        Assert.Contains(phrase, Loc.T("vpn.notify", "WindscribeWireguard"));
        Assert.Contains("• WindscribeWireguard", Loc.T("vpn.confirm", "• WindscribeWireguard"));
        Assert.Contains("may lose its connection", Loc.Format(Loc.English, "vpn.confirm", "x"));
        Assert.DoesNotContain("不能同时使用", Loc.T("vpn.confirm", "x"));
    }
}

/// <summary>After the hotspot stops / Revert: ICS must no longer share anything and IPv4 forwarding must be Disabled again.</summary>
public class SharingCheckTests
{
    private static IcsRecord Ics(string name, bool isPublic, bool isPrivate, bool exists = true) =>
        new("{" + Guid.NewGuid() + "}", name, isPublic, isPrivate, exists, "");

    [Fact]
    public void Analyze_NothingLeft_IsClean()
    {
        var rep = SharingCheck.Analyze(
            new[] { Ics("WLAN", false, false), Ics("Ghost", true, false, exists: false) },
            new[] { new ForwardingState("WLAN", 12, false), new ForwardingState("WLAN 2", 18, null) },
            Array.Empty<string>());
        Assert.True(rep.Clean);
        Assert.Equal(new[] { "WLAN" }, rep.Checked);
        Assert.Equal("已确认 Windows ICS 没有共享任何网卡，WLAN 的 IPv4 转发为 Disabled", rep.OkLine());
    }

    [Fact]
    public void OkLine_NothingReadable_SaysIcsOnly()
    {
        var rep = SharingCheck.Analyze(Array.Empty<IcsRecord>(), new[] { new ForwardingState("WLAN", 12, null) }, Array.Empty<string>());
        Assert.True(rep.Clean);
        Assert.Equal(Loc.T("sharing.okIcsOnly"), rep.OkLine());
    }

    [Fact]
    public void Analyze_IcsStillSharing_ReportsAndAdvisesRestart()
    {
        var rep = SharingCheck.Analyze(
            new[] { Ics("WLAN", true, false), Ics("Local Area Connection* 12", false, true) },
            new[] { new ForwardingState("WLAN", 12, false) },
            Array.Empty<string>());
        Assert.False(rep.Clean);
        Assert.Equal(new[]
        {
            "Windows ICS 仍在共享 WLAN（被共享的上网网卡）",
            "Windows ICS 仍在共享 Local Area Connection* 12（家庭网络一侧）",
        }, rep.ProblemLines());
        Assert.Equal(new[] { Loc.T("sharing.vpnNote"), Loc.T("sharing.adviceRestart") }, rep.AdviceLines());
    }

    [Fact]
    public void Analyze_ForwardingLeftOnByWindows_AdvisesRestart()
    {
        var rep = SharingCheck.Analyze(Array.Empty<IcsRecord>(), new[] { new ForwardingState("WLAN", 12, true) }, Array.Empty<string>());
        Assert.False(rep.Clean);
        Assert.Equal(new[] { "WLAN 的 IPv4 转发仍为 Enabled" }, rep.ProblemLines());
        Assert.Equal(new[] { Loc.T("sharing.vpnNote"), Loc.T("sharing.adviceRestart") }, rep.AdviceLines());
    }

    [Fact]
    public void Analyze_ForwardingLeftOnByApp_AdvisesRevert()
    {
        var rep = SharingCheck.Analyze(Array.Empty<IcsRecord>(), new[] { new ForwardingState("WLAN", 12, true) }, new[] { "wlan" });
        Assert.Equal(new[] { "WLAN 的 IPv4 转发仍为 Enabled（由本软件开启）" }, rep.ProblemLines());
        Assert.Equal(new[] { Loc.T("sharing.vpnNote"), Loc.T("sharing.adviceRevert") }, rep.AdviceLines());
        Assert.Contains("还原 / 卸载", rep.Text());
        Assert.Equal(string.Join("\n", rep.ProblemLines().Concat(rep.AdviceLines())), rep.Text());
    }

    [Fact]
    public void AdaptersToCheck_UpPhysicalAdaptersPlusTheOnesTheAppChanged()
    {
        var adapters = new[]
        {
            Adapters.Wlan(), Adapters.Dongle(), Adapters.ZeroTier(), Adapters.WifiDirect(), Adapters.Windscribe(), Adapters.HyperV(),
            Adapters.Ethernet(up: false, ifIndex: 7),
            new NetAdapterSample("Ethernet 2", "Intel(R) Ethernet Controller I225-V", NetworkInterfaceType.Ethernet, false, 8),
            new NetAdapterSample("Ethernet 3", "Intel(R) Ethernet Controller I225-V #2", NetworkInterfaceType.Ethernet, true, -1),
            Adapters.Wlan(),
        };
        var names = SharingCheck.AdaptersToCheck(adapters, new[] { "Ethernet" }).Select(a => a.Name);
        Assert.Equal(new[] { "WLAN", "WLAN 2", "Ethernet" }, names);
    }

    private static SharingReport Dirty() => SharingCheck.Analyze(Array.Empty<IcsRecord>(), new[] { new ForwardingState("WLAN", 12, true) }, Array.Empty<string>());
    private static SharingReport CleanReport() => SharingCheck.Analyze(Array.Empty<IcsRecord>(), new[] { new ForwardingState("WLAN", 12, false) }, Array.Empty<string>());

    [Fact]
    public async Task WaitUntilClean_ReturnsAsSoonAsWindowsTurnsSharingOff()
    {
        int calls = 0;
        var rep = await SharingCheck.WaitUntilCleanAsync(() => ++calls < 3 ? Dirty() : CleanReport(), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1));
        Assert.NotNull(rep);
        Assert.True(rep!.Clean);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitUntilClean_TimesOut_WithTheLastReport()
    {
        int calls = 0;
        var rep = await SharingCheck.WaitUntilCleanAsync(() => { calls++; return Dirty(); }, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        Assert.NotNull(rep);
        Assert.False(rep!.Clean);
        Assert.True(calls >= 2, $"checked {calls} time(s)");
    }

    [Fact]
    public async Task WaitUntilClean_Aborted_ReturnsNull()
    {
        int calls = 0;
        bool hotspotBack = false;
        var rep = await SharingCheck.WaitUntilCleanAsync(() => { calls++; hotspotBack = true; return Dirty(); },
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1), abort: () => hotspotBack);
        Assert.Null(rep);
        Assert.Equal(1, calls);

        calls = 0;
        Assert.Null(await SharingCheck.WaitUntilCleanAsync(() => { calls++; return CleanReport(); }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), abort: () => true));
        Assert.Equal(0, calls);
    }
}

using VRAirBridge.Core;
using VRAirBridge.Core.Net;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>上行选择：不能把 ZeroTier 之类的虚拟网卡、D-Link 本身、Wi-Fi Direct 当成要共享的上网连接。</summary>
public class UplinkSelectorTests
{
    private const string WlanId = "11111111-1111-1111-1111-111111111111";
    private const string EthId = "22222222-2222-2222-2222-222222222222";
    private const string ZtId = "33333333-3333-3333-3333-333333333333";
    private const string DongleId = "44444444-4444-4444-4444-444444444444";
    private const string WfdId = "55555555-5555-5555-5555-555555555555";
    private const string HyperVId = "66666666-6666-6666-6666-666666666666";

    // 主人电脑上的真实情况：WLAN（Realtek 8852CE，连 gaywifi_5G，网关 192.168.8.1）+ ZeroTier（也有默认路由，跃点 9999）
    private static UplinkCandidate Wlan(UplinkConnectivity conn = UplinkConnectivity.InternetAccess, bool gateway = true, int? metric = 75) =>
        new("gaywifi_5G", "WLAN", "Realtek 8852CE WiFi 6E PCI-E NIC", WlanId, conn, IsWlan: true, IsEthernet: false,
            HasIPv4Gateway: gateway, Metric: metric, HasIPv4Address: true);

    private static UplinkCandidate ZeroTier(UplinkConnectivity conn = UplinkConnectivity.LocalAccess, int? metric = 10034) =>
        new("ZeroTier One [68bea79acf8db002]", "ZeroTier One [68bea79acf8db002]", "ZeroTier Virtual Port", ZtId, conn,
            IsWlan: false, IsEthernet: true, HasIPv4Gateway: true, Metric: metric, HasIPv4Address: true);

    private static UplinkCandidate Ethernet(UplinkConnectivity conn = UplinkConnectivity.InternetAccess, bool gateway = true, int? metric = 25) =>
        new("网络 2", "以太网", "Realtek Gaming 2.5GbE Family Controller", EthId, conn, IsWlan: false, IsEthernet: true,
            HasIPv4Gateway: gateway, Metric: metric, HasIPv4Address: true);

    private static UplinkCandidate HyperV() =>
        new("未识别的网络", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", HyperVId, UplinkConnectivity.LocalAccess,
            IsWlan: false, IsEthernet: true, HasIPv4Gateway: false, Metric: null, HasIPv4Address: true);

    private static UplinkCandidate Dongle() =>
        new("gaywifi_5G", "WLAN 2", "D-Link DWA-F18 VR Air Bridge", DongleId, UplinkConnectivity.InternetAccess,
            IsWlan: true, IsEthernet: false, HasIPv4Gateway: true, Metric: 20, HasIPv4Address: true, IsDongle: true);

    private static UplinkCandidate WifiDirect() =>
        new("VRAirBridge", "本地连接* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2", WfdId, UplinkConnectivity.InternetAccess,
            IsWlan: true, IsEthernet: false, HasIPv4Gateway: true, Metric: 1, HasIPv4Address: true, IsWifiDirect: true);

    private static UplinkChoice Choose(string? windows, string? current, params UplinkCandidate[] c)
    {
        var r = UplinkSelector.Choose(c, windows, current);
        Assert.NotNull(r);
        return r!;
    }

    // ---------------------------------------------------------------- 需求里的六个场景

    [Fact]
    public void ZeroTierReportedByWindows_VsWlanWithGateway_PicksWlan()
    {
        var r = Choose(windows: ZtId, current: null, ZeroTier(), Wlan());
        Assert.Equal(WlanId, r.Candidate.AdapterId);
        Assert.False(r.IsFallback);

        // 即使 ZeroTier 报告能上网、跃点还更小，也不用它
        r = Choose(windows: ZtId, current: null, ZeroTier(UplinkConnectivity.InternetAccess, metric: 5), Wlan(metric: 75));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    [Fact]
    public void EthernetMetric25_BeatsWifiMetric35()
    {
        var r = Choose(windows: WlanId, current: null, Wlan(metric: 35), Ethernet(metric: 25));
        Assert.Equal(EthId, r.Candidate.AdapterId);
        // 反过来 Wi-Fi 跃点更小时选 Wi-Fi
        r = Choose(windows: EthId, current: null, Wlan(metric: 20), Ethernet(metric: 25));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    [Fact]
    public void OnlyVirtualAdapters_FallBackToWindowsDefault()
    {
        var r = Choose(windows: ZtId, current: null, ZeroTier(), HyperV());
        Assert.Equal(ZtId, r.Candidate.AdapterId);
        Assert.True(r.IsFallback);

        // Windows 没报告上网连接：退回最好的已连接虚拟网卡（以前的行为）
        r = Choose(windows: null, current: null, HyperV(), ZeroTier());
        Assert.True(r.IsFallback);
        Assert.Equal(ZtId, r.Candidate.AdapterId);   // 有默认网关的排前面
    }

    [Fact]
    public void Dongle_IsExcluded_EvenWhenPinnedAndOnline()
    {
        // 钉住信道实验让 D-Link 也连上了路由器：仍然必须用电脑自己的上网连接开热点
        var r = Choose(windows: DongleId, current: null, Dongle(), Wlan());
        Assert.Equal(WlanId, r.Candidate.AdapterId);
        Assert.Equal(UplinkExclusion.Dongle, UplinkSelector.Exclusion(Dongle(), out _));
        // 只剩 D-Link：也不退回它
        Assert.Null(UplinkSelector.Choose(new[] { Dongle() }, DongleId));
    }

    [Fact]
    public void WifiDirect_IsExcluded()
    {
        var r = Choose(windows: WfdId, current: null, WifiDirect(), Wlan());
        Assert.Equal(WlanId, r.Candidate.AdapterId);
        Assert.Equal(UplinkExclusion.WifiDirect, UplinkSelector.Exclusion(WifiDirect(), out _));
        // 描述里是 Wi-Fi Direct 但没打标记，也排除
        var unflagged = WifiDirect() with { IsWifiDirect = false };
        Assert.Equal(UplinkExclusion.WifiDirect, UplinkSelector.Exclusion(unflagged, out _));
        Assert.Null(UplinkSelector.Choose(new[] { unflagged }, WfdId));
    }

    [Fact]
    public void LocalAccessOnlyEthernet_LosesTo_InternetWifi()
    {
        var r = Choose(windows: EthId, current: null, Ethernet(UplinkConnectivity.LocalAccess, metric: 5), Wlan(metric: 75));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    // ---------------------------------------------------------------- 排序细节

    [Fact]
    public void Ranking_ConnectivityThenGatewayThenMetricThenPhysical()
    {
        Assert.True(UplinkSelector.ConnectivityRank(UplinkConnectivity.InternetAccess) > UplinkSelector.ConnectivityRank(UplinkConnectivity.LocalAccess));
        Assert.True(UplinkSelector.ConnectivityRank(UplinkConnectivity.LocalAccess) > UplinkSelector.ConnectivityRank(UplinkConnectivity.ConstrainedInternetAccess));
        Assert.True(UplinkSelector.ConnectivityRank(UplinkConnectivity.ConstrainedInternetAccess) > UplinkSelector.ConnectivityRank(UplinkConnectivity.None));

        // 有网关的优先于跃点更小但没网关的
        var r = Choose(null, null, Ethernet(gateway: false, metric: 5), Wlan(metric: 75));
        Assert.Equal(WlanId, r.Candidate.AdapterId);

        // 其它都一样：物理网卡优先于非以太网 / Wi-Fi 的（例如手机蜂窝）
        var wwan = new UplinkCandidate("Carrier", "手机网络", "Generic Mobile Broadband Adapter", "77777777-7777-7777-7777-777777777777",
            UplinkConnectivity.InternetAccess, IsWlan: false, IsEthernet: false, HasIPv4Gateway: true, Metric: 75, HasIPv4Address: true);
        r = Choose(null, null, wwan, Wlan(metric: 75));
        Assert.Equal(WlanId, r.Candidate.AdapterId);

        // 没连上的不选
        Assert.Null(UplinkSelector.Choose(new[] { Wlan(UplinkConnectivity.None) }));
        Assert.Null(UplinkSelector.Choose(Array.Empty<UplinkCandidate>()));
    }

    [Theory]
    [InlineData("ZeroTier One [68bea79acf8db002]", "ZeroTier Virtual Port", "ZeroTier")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "Hyper-V")]
    [InlineData("Tailscale", "Tailscale Tunnel", "Tailscale")]
    [InlineData("以太网 3", "TAP-Windows Adapter V9", "TAP-Windows")]
    [InlineData("以太网 4", "wintun userspace tunnel", "Wintun")]
    [InlineData("WireGuard", "WireGuard Tunnel", "WireGuard")]
    [InlineData("以太网 5", "VMware Virtual Ethernet Adapter for VMnet8", "VMware")]
    [InlineData("以太网 6", "VirtualBox Host-Only Ethernet Adapter", "VirtualBox")]
    [InlineData("clash", "Clash Tunnel", "Clash")]
    [InlineData("Mihomo", "Mihomo Tunnel", "Mihomo")]
    [InlineData("sing-box", "sing-box TUN", "sing-box")]
    [InlineData("Npcap Loopback Adapter", "Npcap Loopback Adapter", "Npcap")]
    [InlineData("蓝牙网络连接", "Bluetooth Device (Personal Area Network)", "Bluetooth")]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", "Radmin")]
    [InlineData("Hamachi", "LogMeIn Hamachi Virtual Ethernet Adapter", "Hamachi")]
    [InlineData("以太网 7", "Microsoft Kernel Debug Network Adapter", "Kernel Debug")]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", "Loopback")]
    [InlineData("OpenVPN Data Channel Offload", "OpenVPN Data Channel Offload", "OpenVPN")]
    [InlineData("WLAN", "Realtek 8852CE WiFi 6E PCI-E NIC", null)]
    [InlineData("以太网", "Intel(R) Ethernet Connection (17) I219-V", null)]
    [InlineData("以太网 2", "Realtek USB GbE Family Controller", null)]
    public void VirtualMarker_MatchesNameOrDescription_CaseInsensitive(string name, string desc, string? expected)
    {
        var m = UplinkSelector.VirtualMarker(name, desc);
        if (expected == null) Assert.Null(m);
        else Assert.Equal(expected, m, ignoreCase: true);
    }

    [Fact]
    public void VirtualMarker_IgnoresProfileName()
    {
        // Wi-Fi 的 SSID 可以随便起（例如 “Clash_5G”），不能因此把真正的 WLAN 当成虚拟网卡
        var wlan = Wlan() with { ProfileName = "Clash_5G" };
        Assert.True(UplinkSelector.IsEligible(wlan));
        var r = Choose(null, null, wlan, ZeroTier());
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    [Fact]
    public void Loopback_AndDisconnected_AreExcluded()
    {
        var loop = Wlan() with { AdapterId = "88888888-8888-8888-8888-888888888888", AdapterName = "lo", AdapterDescription = "x", IsLoopback = true };
        Assert.Equal(UplinkExclusion.Loopback, UplinkSelector.Exclusion(loop, out _));
        var down = Wlan() with { IsUp = false };
        Assert.Equal(UplinkExclusion.NotConnected, UplinkSelector.Exclusion(down, out _));
        Assert.Equal(UplinkExclusion.Virtual, UplinkSelector.Exclusion(ZeroTier(), out var marker));
        Assert.Equal("ZeroTier", marker);
    }

    // ---------------------------------------------------------------- 热点运行中保持稳定

    [Fact]
    public void Sticky_KeepsCurrentUplink_WhileItHasGatewayAndConnectivity()
    {
        // 热点开在 WLAN 上时插了网线（跃点更小）：不换，避免热点上行来回跳
        var r = Choose(windows: EthId, current: WlanId, Wlan(metric: 75), Ethernet(metric: 25));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
        Assert.True(r.Kept);

        // 大括号 / 大小写不同也算同一块网卡
        r = Choose(windows: EthId, current: "{" + WlanId.ToUpperInvariant() + "}", Wlan(metric: 75), Ethernet(metric: 25));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    [Fact]
    public void Sticky_Switches_WhenCurrentLosesGatewayOrConnectivity()
    {
        var r = Choose(windows: EthId, current: WlanId, Wlan(gateway: false), Ethernet());
        Assert.Equal(EthId, r.Candidate.AdapterId);
        Assert.False(r.Kept);

        r = Choose(windows: EthId, current: WlanId, Wlan(UplinkConnectivity.LocalAccess), Ethernet());
        Assert.Equal(EthId, r.Candidate.AdapterId);

        r = Choose(windows: EthId, current: WlanId, Wlan(UplinkConnectivity.None), Ethernet());
        Assert.Equal(EthId, r.Candidate.AdapterId);

        // 当前的网卡不见了
        r = Choose(windows: EthId, current: WlanId, Ethernet());
        Assert.Equal(EthId, r.Candidate.AdapterId);
    }

    [Fact]
    public void Sticky_NeverKeepsAnExcludedAdapter()
    {
        // 旧版本把 ZeroTier 当成了当前上行：有物理网卡可用时立刻换掉
        var r = Choose(windows: ZtId, current: ZtId, ZeroTier(), Wlan());
        Assert.Equal(WlanId, r.Candidate.AdapterId);
        Assert.False(r.IsFallback);
    }

    [Fact]
    public void Sticky_AllRouterInternetDown_KeepsCurrent()
    {
        // 路由器外网断了：两块网卡都只有 LocalAccess，不在它们之间跳
        var r = Choose(windows: EthId, current: WlanId, Wlan(UplinkConnectivity.LocalAccess), Ethernet(UplinkConnectivity.LocalAccess));
        Assert.Equal(WlanId, r.Candidate.AdapterId);
    }

    // ---------------------------------------------------------------- 日志文字

    [Fact]
    public void ChosenLogLine_ReadsLikeTheSpec()
    {
        var details = string.Join(Loc.T("common.comma"), Loc.T("uplink.hasGateway"), Loc.T("uplink.metric", 25));
        Assert.Equal("上行: WLAN (gaywifi_5G) — 有默认网关，跃点 25", Loc.T("uplink.chosen", "WLAN", "gaywifi_5G", details));
    }
}

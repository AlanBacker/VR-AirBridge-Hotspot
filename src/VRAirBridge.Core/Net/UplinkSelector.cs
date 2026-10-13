namespace VRAirBridge.Core.Net;

/// <summary>与 WinRT NetworkConnectivityLevel 数值一致（便于强转），但不依赖 WinRT，方便单元测试。</summary>
public enum UplinkConnectivity { None = 0, LocalAccess = 1, ConstrainedInternetAccess = 2, InternetAccess = 3 }

/// <summary>候选上行不能用的原因。</summary>
public enum UplinkExclusion { None, NotConnected, WifiDirect, Dongle, Loopback, Virtual }

/// <summary>
/// 一个可能的上行（给热点供网的）连接：Windows 连接配置文件 + 对应网卡的信息。
/// Metric = IPv4 默认路由的有效跃点数（路由跃点 + 接口跃点），没有默认路由时为 null。
/// </summary>
public sealed record UplinkCandidate(
    string ProfileName,
    string AdapterName,
    string AdapterDescription,
    string AdapterId,
    UplinkConnectivity Connectivity,
    bool IsWlan,
    bool IsEthernet,
    bool HasIPv4Gateway,
    int? Metric,
    bool HasIPv4Address,
    bool IsDongle = false,
    bool IsWifiDirect = false,
    bool IsLoopback = false,
    bool IsUp = true)
{
    /// <summary>物理以太网 / Wi-Fi（虚拟网卡在排序前已经被排除，这里只看介质类型）。</summary>
    public bool IsPhysical => IsWlan || IsEthernet;
}

/// <summary>选择结果。IsFallback = 没有合格的物理网卡，退回到了 Windows 报告的连接（或最好的虚拟网卡）；Kept = 沿用了当前上行。</summary>
public sealed record UplinkChoice(UplinkCandidate Candidate, bool IsFallback, bool Kept);

/// <summary>
/// 有意识地选择热点要共享的上行连接（纯函数，可单元测试）。
/// Windows 的 GetInternetConnectionProfile() 有时会返回 ZeroTier / Tailscale / Hyper-V 之类的虚拟网卡，
/// 用它开热点、配 DNS、开转发都会出错，所以这里自己排序：
/// 1) 排除 Wi-Fi Direct 虚拟网卡、D-Link 本身、环回、各种 VPN / 虚拟机 / 抓包网卡；
/// 2) 连通性 InternetAccess &gt; LocalAccess &gt; ConstrainedInternetAccess；
/// 3) 有 IPv4 默认网关优先；4) 跃点数小的优先；5) 物理以太网 / Wi-Fi 优先。
/// 只有一个物理网卡都不合格时，才退回 Windows 报告的连接。
/// </summary>
public static class UplinkSelector
{
    /// <summary>名称或描述里含这些字样（不区分大小写）的网卡视为虚拟网卡，不作为上行。</summary>
    public static readonly IReadOnlyList<string> VirtualMarkers = new[]
    {
        "ZeroTier", "Tailscale", "Hyper-V", "vEthernet", "VMware", "VirtualBox", "TAP-Windows", "Wintun", "WireGuard",
        "OpenVPN", "Clash", "Mihomo", "sing-box", "Npcap", "Bluetooth", "Radmin", "Hamachi", "Kernel Debug", "Loopback",
        "WAN Miniport", "Teredo", "6to4", "IP-HTTPS", "ISATAP", "Pseudo-Interface", "NordLynx", "Mullvad", "SoftEther",
        "Virtual Ethernet", "Virtual Adapter", "Virtual Network", "Virtual Switch", "Virtual Miniport", "Virtual Port",
    };

    /// <summary>Wi-Fi Direct 虚拟网卡（热点自己的 GO 网卡）的描述字样。</summary>
    public const string WifiDirectMarker = "Wi-Fi Direct";

    /// <summary>名称或描述命中的第一个虚拟网卡字样；不是虚拟网卡返回 null。不看配置文件名（Wi-Fi 的 SSID 可以随便起）。</summary>
    public static string? VirtualMarker(string? adapterName, string? adapterDescription)
    {
        foreach (var m in VirtualMarkers)
        {
            if ((adapterName ?? "").Contains(m, StringComparison.OrdinalIgnoreCase) ||
                (adapterDescription ?? "").Contains(m, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }

    public static bool IsWifiDirectDescription(string? description) =>
        (description ?? "").Contains(WifiDirectMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>这块网卡能否被本软件当作上行来开 IPv4 转发（虚拟网卡、Wi-Fi Direct 都不行）。</summary>
    public static bool IsVirtualOrWifiDirect(string? adapterName, string? adapterDescription) =>
        IsWifiDirectDescription(adapterDescription) || VirtualMarker(adapterName, adapterDescription) != null;

    /// <summary>候选为什么不能用作上行（UplinkExclusion.None = 可以用）。marker = 命中的虚拟网卡字样。</summary>
    public static UplinkExclusion Exclusion(UplinkCandidate c, out string? marker)
    {
        marker = null;
        if (c.IsWifiDirect || IsWifiDirectDescription(c.AdapterDescription)) return UplinkExclusion.WifiDirect;
        if (c.IsDongle) return UplinkExclusion.Dongle;
        if (c.IsLoopback) return UplinkExclusion.Loopback;
        marker = VirtualMarker(c.AdapterName, c.AdapterDescription);
        if (marker != null) return UplinkExclusion.Virtual;
        if (!c.IsUp || c.Connectivity == UplinkConnectivity.None) return UplinkExclusion.NotConnected;
        return UplinkExclusion.None;
    }

    public static bool IsEligible(UplinkCandidate c) => Exclusion(c, out _) == UplinkExclusion.None;

    /// <summary>连通性排名：InternetAccess 3 &gt; LocalAccess 2 &gt; ConstrainedInternetAccess 1 &gt; None 0。</summary>
    public static int ConnectivityRank(UplinkConnectivity c) => c switch
    {
        UplinkConnectivity.InternetAccess => 3,
        UplinkConnectivity.LocalAccess => 2,
        UplinkConnectivity.ConstrainedInternetAccess => 1,
        _ => 0,
    };

    /// <summary>按优先级排序（最好的在前）。</summary>
    public static List<UplinkCandidate> Rank(IEnumerable<UplinkCandidate> candidates) =>
        candidates
            .OrderByDescending(c => ConnectivityRank(c.Connectivity))
            .ThenByDescending(c => c.HasIPv4Gateway)
            .ThenBy(c => c.Metric ?? int.MaxValue)
            .ThenByDescending(c => c.IsPhysical)
            .ThenByDescending(c => c.HasIPv4Address)
            .ThenBy(c => c.AdapterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.AdapterId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool SameAdapter(string? a, string? b)
    {
        static string N(string? s) => (s ?? "").Trim().TrimStart('{').TrimEnd('}');
        var x = N(a);
        return x.Length > 0 && string.Equals(x, N(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 选上行。windowsDefaultAdapterId = GetInternetConnectionProfile() 对应的网卡；
    /// currentAdapterId = 热点运行中正在用的上行（为了稳定：只有它丢了默认网关 / 连通性，或者别的网卡连通性更好时才换）。
    /// 一个都选不出来返回 null。
    /// </summary>
    public static UplinkChoice? Choose(IReadOnlyList<UplinkCandidate> candidates, string? windowsDefaultAdapterId = null, string? currentAdapterId = null)
    {
        var eligible = Rank(candidates.Where(IsEligible));
        if (eligible.Count > 0)
        {
            var best = eligible[0];
            var cur = currentAdapterId == null ? null : eligible.FirstOrDefault(c => SameAdapter(c.AdapterId, currentAdapterId));
            if (cur != null && cur.HasIPv4Gateway && ConnectivityRank(cur.Connectivity) >= ConnectivityRank(best.Connectivity))
                return new UplinkChoice(cur, false, true);
            return new UplinkChoice(best, false, false);
        }

        // 没有合格的物理网卡：退回 Windows 报告的连接（虚拟网卡也只好用了），但绝不用 D-Link、Wi-Fi Direct、环回
        var usable = candidates.Where(c =>
        {
            var x = Exclusion(c, out _);
            return x == UplinkExclusion.None || (x == UplinkExclusion.Virtual && c.IsUp && c.Connectivity != UplinkConnectivity.None);
        }).ToList();
        if (usable.Count == 0) return null;
        var keep = currentAdapterId == null ? null : usable.FirstOrDefault(c => SameAdapter(c.AdapterId, currentAdapterId));
        if (keep != null && keep.HasIPv4Gateway) return new UplinkChoice(keep, true, true);
        var win = windowsDefaultAdapterId == null ? null : usable.FirstOrDefault(c => SameAdapter(c.AdapterId, windowsDefaultAdapterId));
        if (win != null) return new UplinkChoice(win, true, false);
        return new UplinkChoice(Rank(usable)[0], true, false);
    }
}

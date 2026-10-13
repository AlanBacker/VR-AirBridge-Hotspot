using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;
using VRAirBridge.Core.Wlan;

namespace VRAirBridge.Core.Hotspot;

/// <summary>附近的一个 Wi-Fi（BSS）。Channel = 主信道，WidthMHz 来自信标里的 HT/VHT/HE 信息元素。</summary>
public sealed record NeighborBss(string Ssid, string Bssid, int Channel, int FreqMHz, int WidthMHz, int SignalDbm, string Phy)
{
    public bool Is5GHz => FreqMHz is >= 5150 and < 5925;
    public string DisplayName => string.IsNullOrEmpty(Ssid) ? Loc.T("channelInfo.hidden", Bssid) : Ssid;
}

/// <summary>5GHz 的一个 80MHz 块（例如 36–48）。DFS 块热点不会使用。</summary>
public sealed record ChannelBlock(int First, int Last, bool Dfs)
{
    public string Name => $"{First}–{Last}";
    public bool Contains(int channel) => channel >= First && channel <= Last;
}

public sealed record BlockOccupancy(ChannelBlock Block, int Networks, int? StrongestDbm, IReadOnlyList<string> Names);

/// <summary>“钉住到网络”下拉框的一个候选。</summary>
public sealed record PinCandidate(string Ssid, int? Channel, bool IsUplink, bool HasProfile)
{
    public bool Dfs => Channel is int c && ChannelInfo.IsDfs(c);
    public bool Is24GHz => Channel is int c && c <= 14;
    public bool Usable => HasProfile && !Dfs && !Is24GHz;
    /// <summary>"HomeWiFi-5G (channel 36 · PC is connected)": all qualifiers in one bracket, joined by a separator.</summary>
    public string Display
    {
        get
        {
            var parts = new List<string>(3);
            if (Channel is int ch) parts.Add(Loc.T("channelInfo.channel", ch));
            if (IsUplink) parts.Add(ChannelStrings.CandidateUplink);
            var why = Dfs ? ChannelStrings.CandidateDfs : Is24GHz ? ChannelStrings.Candidate24 : !HasProfile ? ChannelStrings.CandidateNoProfile : null;
            if (why != null) parts.Add(why);
            return parts.Count == 0 ? Ssid : Loc.T("channel.candidateFormat", Ssid, string.Join(Loc.T("channel.candidateSep"), parts));
        }
    }
}

/// <summary>一次信道扫描的结果。</summary>
public sealed record ChannelReport(
    DateTime Time,
    string? ScannerName,
    int? GoChannel,
    int? GoWidthMHz,
    IReadOnlyList<NeighborBss> Neighbors,
    IReadOnlyList<BlockOccupancy> Occupancy,
    string? UplinkSsid,
    int? UplinkChannel,
    IReadOnlyList<PinCandidate> Candidates,
    string? Error)
{
    public string GoText => GoChannel is int c
        ? Loc.T("channelInfo.channelMhz", c, GoWidthMHz ?? 20, ChannelInfo.BlockOf(c)?.Name ?? Loc.T("channelInfo.not80MhzBlock"))
        : ChannelStrings.GoUnknown;

    public string OccupancyLine => ChannelInfo.FormatOccupancy(Occupancy);

    /// <summary>紧凑的邻居列表：“名字 / 信道 / 信号”，按信号强弱。</summary>
    public IEnumerable<string> NeighborLines(int max = 8) =>
        Neighbors.Take(max).Select(n => Loc.T("channelInfo.channelDbm", n.DisplayName, n.Channel, (n.WidthMHz > 20 ? $"/{n.WidthMHz}" : ""), n.SignalDbm, (ChannelInfo.IsDfs(n.Channel) ? "  DFS" : "")));
}

/// <summary>
/// 信道面板：用电脑上的无线网卡（优先非 D-Link 的那块）扫描 5GHz，报告热点（GO）当前的信道/带宽、
/// 附近的 5GHz 网络和每个 80MHz 块的占用情况。只在启动软件、用户点“扫描”、核对信标时扫描，不会定时扫描。
/// Windows 移动热点没有设置信道的接口；在 5GHz 上它只会用 36–48 和 149–161 这两个非 DFS 的 80MHz 块。
/// </summary>
public static class ChannelInfo
{
    public static readonly IReadOnlyList<ChannelBlock> Blocks5G = new[]
    {
        new ChannelBlock(36, 48, false),
        new ChannelBlock(52, 64, true),
        new ChannelBlock(100, 112, true),
        new ChannelBlock(116, 128, true),
        new ChannelBlock(132, 144, true),
        new ChannelBlock(149, 161, false),
    };

    private static readonly object _lock = new();
    private static ChannelReport? _last;

    public static ChannelReport? Last { get { lock (_lock) return _last; } }
    public static event Action<ChannelReport>? Updated;

    // ================================================================ 纯函数（单元测试）

    /// <summary>信道所在的 80MHz 块；165 及以上、2.4GHz 返回 null。</summary>
    public static ChannelBlock? BlockOf(int channel) => Blocks5G.FirstOrDefault(b => b.Contains(channel));

    /// <summary>DFS 信道（52–144）：需要雷达检测，Windows 热点不会使用。</summary>
    public static bool IsDfs(int channel) => channel >= 52 && channel <= 144;

    /// <summary>一个 BSS 覆盖到的 80MHz 块（160MHz 的网络会占两块）。</summary>
    public static IReadOnlyList<ChannelBlock> BlocksCovered(int channel, int widthMHz)
    {
        var b = BlockOf(channel);
        if (b == null) return Array.Empty<ChannelBlock>();
        if (widthMHz < 160) return new[] { b };
        // 160MHz：36–64、100–128 两组（149–177 的另一半不在列表里）
        int pair = b.First switch { 36 => 1, 52 => 0, 100 => 3, 116 => 2, _ => -1 };
        return pair >= 0 ? new[] { b, Blocks5G[pair] }.OrderBy(x => x.First).ToArray() : new[] { b };
    }

    /// <summary>每个 80MHz 块里有多少个网络（按 BSSID 计）、最强的信号。</summary>
    public static IReadOnlyList<BlockOccupancy> Occupancy(IEnumerable<NeighborBss> neighbors)
    {
        var list = neighbors.ToList();
        return Blocks5G.Select(b =>
        {
            var inBlock = list.Where(n => BlocksCovered(n.Channel, n.WidthMHz).Contains(b)).ToList();
            return new BlockOccupancy(b, inBlock.Count, inBlock.Count == 0 ? null : inBlock.Max(n => n.SignalDbm),
                inBlock.Select(n => n.DisplayName).Distinct().ToList());
        }).ToList();
    }

    /// <summary>一行文字：“36–48：3 个（最强 -52 dBm） · 149–161：空闲 · 52–144：DFS，热点不会使用（共 2 个）”。</summary>
    public static string FormatOccupancy(IReadOnlyList<BlockOccupancy> occ)
    {
        var parts = new List<string>();
        foreach (var o in occ.Where(o => !o.Block.Dfs))
            parts.Add(o.Networks == 0 ? Loc.T("common.labelValue", o.Block.Name, ChannelStrings.BlockFree) : Loc.T("channelInfo.networksStrongestDbm", o.Block.Name, o.Networks, o.StrongestDbm));
        var dfs = occ.Where(o => o.Block.Dfs).ToList();
        if (dfs.Count > 0)
        {
            int n = dfs.Sum(o => o.Networks);
            parts.Add(Loc.T("channelInfo.text", dfs.Min(o => o.Block.First), dfs.Max(o => o.Block.Last), ChannelStrings.DfsBlock) + (n > 0 ? Loc.T("channelInfo.total", n) : ""));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>把扫描结果转成 5GHz 邻居列表（去掉自己的热点；同一 BSSID 只留一条；按信号从强到弱）。</summary>
    public static List<NeighborBss> ToNeighbors(IEnumerable<WlanBss> bss, string? excludeMac)
    {
        var ex = string.IsNullOrEmpty(excludeMac) ? null : MacUtil.Normalize(excludeMac);
        return bss.Where(b => ex == null || MacUtil.Normalize(b.Bssid) != ex)
            .Select(b =>
            {
                var info = BeaconParser.Parse(b.Ies, b.FreqKHz);
                return new NeighborBss(b.Ssid, b.Bssid, info.Channel, info.FreqMHz, info.WidthMHz, b.Rssi, info.Phy);
            })
            .Where(n => n.Is5GHz)
            .GroupBy(n => MacUtil.Normalize(n.Bssid))
            .Select(g => g.First())
            .OrderByDescending(n => n.SignalDbm)
            .ToList();
    }

    /// <summary>“钉住到网络”的候选：附近的 5GHz SSID + 电脑正在连的 SSID（去重，排除自己的热点名）。</summary>
    public static List<PinCandidate> BuildCandidates(IEnumerable<NeighborBss> neighbors, string? uplinkSsid, int? uplinkChannel,
        ICollection<string> profileSsids, string? ownSsid)
    {
        var res = new List<PinCandidate>();
        bool Has(string s) => profileSsids.Contains(s);
        if (!string.IsNullOrEmpty(uplinkSsid) && uplinkSsid != ownSsid)
            res.Add(new PinCandidate(uplinkSsid, uplinkChannel, true, Has(uplinkSsid)));
        foreach (var g in neighbors.Where(n => !string.IsNullOrEmpty(n.Ssid) && n.Ssid != ownSsid).GroupBy(n => n.Ssid))
        {
            if (res.Any(r => r.Ssid == g.Key)) continue;
            var best = g.OrderByDescending(n => n.SignalDbm).First();
            res.Add(new PinCandidate(g.Key, best.Channel, false, Has(g.Key)));
        }
        return res.OrderByDescending(c => c.IsUplink).ThenByDescending(c => c.Usable).ToList();
    }

    // ================================================================ 扫描

    /// <summary>
    /// 扫描并更新 <see cref="Last"/>。优先用非 D-Link 的无线网卡；只有 D-Link 时，
    /// 热点开着就不扫描（allowDongle = false），避免打断串流。
    /// </summary>
    public static async Task<ChannelReport> ScanAsync(string? goMac, string? dongleGuid, bool allowDongle, string? ownSsid, CancellationToken ct = default)
    {
        var ifs = WlanNative.EnumInterfaces();
        Guid dg = Guid.Empty;
        if (!string.IsNullOrEmpty(dongleGuid)) Guid.TryParse(dongleGuid, out dg);
        bool IsDongle(WlanInterface i) => i.Guid == dg || i.Description.Contains(DongleInfo.DescriptionKeyword, StringComparison.OrdinalIgnoreCase);
        var scanner = ifs.FirstOrDefault(i => !IsDongle(i)) ?? (allowDongle ? ifs.FirstOrDefault() : null);
        if (scanner == null)
        {
            var err = ifs.Count > 0 ? ChannelStrings.DongleOnlyScanNote : ChannelStrings.NoScanner;
            return Publish(Empty(err, ownSsid, dg));
        }
        try
        {
            try { WlanNative.Scan(scanner.Guid); } catch { }
            await Task.Delay(4500, ct);
            var all = WlanNative.GetBssList(scanner.Guid);
            return Record(all, scanner.Description, goMac, dongleGuid, ownSsid);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Debug(Loc.T("channelInfo.channelScanFailed") + ex.Message);
            return Publish(Empty(ChannelStrings.ScanFailedPrefix + ex.Message, ownSsid, dg));
        }
    }

    private static ChannelReport Empty(string error, string? ownSsid, Guid dongle)
    {
        var (upSsid, upCh) = UplinkConnection(dongle);
        var profiles = ProfileSsids(dongle);
        return new ChannelReport(DateTime.Now, null, null, null, Array.Empty<NeighborBss>(), Occupancy(Array.Empty<NeighborBss>()),
            upSsid, upCh, BuildCandidates(Array.Empty<NeighborBss>(), upSsid, upCh, profiles, ownSsid), error);
    }

    /// <summary>根据一份 BSS 列表（来自扫描或信标核对）生成报告并发布。</summary>
    public static ChannelReport Record(IReadOnlyList<WlanBss> all, string scannerName, string? goMac, string? dongleGuid, string? ownSsid = null)
    {
        Guid dg = Guid.Empty;
        if (!string.IsNullOrEmpty(dongleGuid)) Guid.TryParse(dongleGuid, out dg);
        int? goCh = null, goW = null;
        if (!string.IsNullOrEmpty(goMac))
        {
            var target = MacUtil.Normalize(goMac);
            var hit = all.FirstOrDefault(b => MacUtil.Normalize(b.Bssid) == target);
            if (hit != null)
            {
                var info = BeaconParser.Parse(hit.Ies, hit.FreqKHz);
                goCh = info.Channel; goW = info.WidthMHz;
                ownSsid ??= hit.Ssid;
            }
        }
        var neighbors = ToNeighbors(all, goMac);
        var (upSsid, upCh) = UplinkConnection(dg);
        var profiles = ProfileSsids(dg);
        var rep = new ChannelReport(DateTime.Now, scannerName, goCh, goW, neighbors, Occupancy(neighbors), upSsid, upCh,
            BuildCandidates(neighbors, upSsid, upCh, profiles, ownSsid), null);
        return Publish(rep);
    }

    private static ChannelReport Publish(ChannelReport rep)
    {
        lock (_lock)
        {
            // 没扫到热点信标时保留上一次的 GO 信道（热点信道不会自己变）
            if (rep.GoChannel == null && _last?.GoChannel != null && rep.Error == null && DateTime.Now - _last.Time < TimeSpan.FromMinutes(30))
                rep = rep with { GoChannel = _last.GoChannel, GoWidthMHz = _last.GoWidthMHz };
            _last = rep;
        }
        try { Updated?.Invoke(rep); } catch { }
        return rep;
    }

    /// <summary>热点关了 / 重开之后清掉记住的 GO 信道。</summary>
    public static void ForgetGoChannel()
    {
        lock (_lock) if (_last != null) _last = _last with { GoChannel = null, GoWidthMHz = null };
    }

    /// <summary>非 D-Link 网卡当前连接的 SSID 和信道（电脑自己的 Wi-Fi）。</summary>
    public static (string? Ssid, int? Channel) UplinkConnection(Guid dongle)
    {
        foreach (var i in WlanNative.EnumInterfaces().Where(i => i.Guid != dongle && !i.Description.Contains(DongleInfo.DescriptionKeyword, StringComparison.OrdinalIgnoreCase)))
        {
            var c = WlanNative.QueryConnection(i.Guid);
            if (c != null && c.State == 1 && !string.IsNullOrEmpty(c.Ssid)) return (c.Ssid, WlanNative.QueryChannel(i.Guid));
        }
        return (null, null);
    }

    /// <summary>非 D-Link 网卡上保存的配置文件名（通常等于 SSID），用来判断“电脑有没有这个网络的密码”。</summary>
    private static HashSet<string> ProfileSsids(Guid dongle)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in WlanNative.EnumInterfaces().Where(i => i.Guid != dongle))
            foreach (var n in WlanNative.GetProfileNames(i.Guid)) set.Add(n);
        return set;
    }

    public static IEnumerable<string> DescribeLines(ChannelReport? r)
    {
        if (r == null) { yield return ChannelStrings.NoScan; yield break; }
        yield return Loc.T("channelInfo.scannedAtWithAdapter", r.Time, r.ScannerName ?? Loc.T("channelInfo.none")) + (r.Error != null ? Loc.T("common.commaThen", r.Error) : "");
        yield return $"{ChannelStrings.CurrentLabel}: {r.GoText}";
        if (r.UplinkSsid != null) yield return Loc.T("channelInfo.pcWiFiChannel", r.UplinkSsid, r.UplinkChannel?.ToString() ?? "?");
        yield return $"{ChannelStrings.BlocksLabel}: {r.OccupancyLine}";
        if (r.Neighbors.Count == 0) yield return ChannelStrings.NoNeighbors;
        foreach (var l in r.NeighborLines(12)) yield return "  " + l;
    }
}

using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;
using VRAirBridge.Core.Wlan;

namespace VRAirBridge.Core.Hotspot;

/// <summary>D-Link 和内置网卡扫到的同一批 5 GHz 路由器的信号差（中位数，dB；正数 = D-Link 更弱）。</summary>
public sealed record RxCompare(int Samples, int MedianDelta, int? DongleBest, int ReferenceBest)
{
    /// <summary>D-Link 比内置网卡弱 25 dB 以上：多半是 USB 口位置不好（插在集线器、机箱前面板，或挨着 USB 3 设备）。</summary>
    public bool Weak => Samples >= 2 && MedianDelta >= 25;
}

/// <summary>接收检查的结果：信号差，以及 D-Link 是不是插在 USB 集线器上、同一个集线器上还有哪些设备。</summary>
public sealed record RxReport(RxCompare Compare, string ReferenceName, bool OnHub, IReadOnlyList<string> HubPeers)
{
    /// <summary>通知里的完整说法：弱多少、该插哪；插在集线器上时再说和谁挤在一起。</summary>
    public string Advice => Loc.T("rx.weakText", Compare.MedianDelta) + HubText;

    /// <summary>主界面上的短说法：“该插哪”交给下面的示意图。</summary>
    public string Hint => Loc.T("rx.weakHint", Compare.MedianDelta) + HubText;

    private string HubText => !OnHub ? ""
        : " " + (HubPeers.Count > 0 ? Loc.T("rx.hubPeers", string.Join(Loc.T("common.comma"), HubPeers)) : Loc.T("rx.onHubNoPeers"));
}

/// <summary>
/// 开热点前，用 D-Link 和电脑上另一块无线网卡同时扫描一次，比较同一批路由器的信号。
/// D-Link 插在不好的 USB 口上时（2026-10-13 作者电脑：和键盘、麦克风、移动硬盘挤在一个集线器上），
/// 它几乎听不见手机，内置网卡 −44 dBm 的路由器它只收到 −90 dBm 左右；换到主板背面的口后正常。
/// </summary>
public static class ReceptionCheck
{
    /// <summary>只比较 5 GHz（热点用的频段）；参考网卡收得到（≥ −75 dBm）的才算；参考网卡收得很清楚（≥ −60）而 D-Link 完全没扫到的按 45 dB 算。</summary>
    public static RxCompare? Compare(IEnumerable<WlanBss> dongle, IEnumerable<WlanBss> reference)
    {
        static bool Is5G(WlanBss b) => b.FreqKHz >= 4_900_000 && b.FreqKHz <= 5_950_000;
        static Dictionary<string, int> Best(IEnumerable<WlanBss> l) => l.Where(Is5G)
            .GroupBy(b => MacUtil.Normalize(b.Bssid)).Where(g => g.Key.Length == 12)
            .ToDictionary(g => g.Key, g => g.Max(b => b.Rssi));
        var d = Best(dongle);
        var r = Best(reference);
        var deltas = new List<int>();
        foreach (var (mac, rr) in r)
        {
            if (rr < -75) continue;
            if (d.TryGetValue(mac, out var dr)) deltas.Add(rr - dr);
            else if (rr >= -60) deltas.Add(45);
        }
        if (deltas.Count == 0) return null;
        deltas.Sort();
        int n = deltas.Count;
        int median = n % 2 == 1 ? deltas[n / 2] : (deltas[n / 2 - 1] + deltas[n / 2]) / 2;
        return new RxCompare(n, median, d.Count > 0 ? d.Values.Max() : null, r.Values.Max());
    }

    /// <summary>
    /// 两块网卡同时扫描并比较；信号差时再扫一次确认。没有第二块无线网卡、D-Link 什么都没扫到、或附近没有可比的路由器时返回 null。
    /// 只能在热点关着时调用（热点开着时 D-Link 忙着发信标，扫描不准）。
    /// </summary>
    public static async Task<RxReport?> RunAsync(DongleInfo dongle, CancellationToken ct)
    {
        if (!Guid.TryParse(dongle.Guid, out var dg)) return null;
        var ifs = WlanNative.EnumInterfaces();
        if (!ifs.Any(i => i.Guid == dg)) return null;
        var reference = ifs.FirstOrDefault(i => i.Guid != dg && !i.Description.Contains("DWA-F18", StringComparison.OrdinalIgnoreCase));
        if (reference == null) return null;

        RxCompare? cmp = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var (dl, rl) = await ScanBothAsync(dg, reference.Guid, ct);
            if (dl.Count == 0) { cmp = null; continue; }
            cmp = Compare(dl, rl);
            if (cmp == null || !cmp.Weak) break;
        }
        if (cmp == null) return null;
        var (onHub, peers) = UsbPeers(dongle.PnpId);
        Logger.Info(Loc.T("rx.result", cmp.MedianDelta, cmp.Samples, cmp.DongleBest?.ToString() ?? "-", cmp.ReferenceBest, reference.Description)
            + (onHub ? " · " + Loc.T("rx.onHub", peers.Count > 0 ? string.Join(Loc.T("common.comma"), peers) : "-") : ""));
        return new RxReport(cmp, reference.Description, onHub, peers);
    }

    private static async Task<(List<WlanBss> dongle, List<WlanBss> reference)> ScanBothAsync(Guid dongle, Guid reference, CancellationToken ct)
    {
        try { WlanNative.Scan(dongle); } catch { }
        try { WlanNative.Scan(reference); } catch { }
        await Task.Delay(4500, ct);
        List<WlanBss> d = new(), r = new();
        try { d = WlanNative.GetBssList(dongle); } catch (Exception ex) { Logger.Debug("rx scan (D-Link): " + ex.Message); }
        try { r = WlanNative.GetBssList(reference); } catch (Exception ex) { Logger.Debug("rx scan (reference): " + ex.Message); }
        return (d, r);
    }

    // ---------------------------------------------------------------- USB 集线器上的其它设备（设备树）

    /// <summary>
    /// D-Link 是不是插在 USB 集线器（扩展坞、显示器、机箱前面板的集线器）上，以及同一个集线器上的其它设备名。
    /// 外接集线器的 USB 2 和 USB 3 两半在系统里是两个设备，挂在同一个上级下面、厂商 ID 相同，两边的设备都算。
    /// </summary>
    public static (bool onHub, List<string> peers) UsbPeers(string? pnpId)
    {
        var peers = new List<string>();
        try
        {
            if (DevNode.Locate(pnpId) is not uint self || DevNode.Parent(self) is not uint hub) return (false, peers);
            var hubId = DevNode.Id(hub);
            if (hubId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) return (false, peers);

            var hubs = new List<uint> { hub };
            var vid = Vid(hubId);
            if (vid != null && DevNode.Parent(hub) is uint up)
                hubs.AddRange(DevNode.Children(up).Where(h => h != hub && IsHub(h) && Vid(DevNode.Id(h)) == vid));

            foreach (var h in hubs)
                foreach (var c in DevNode.Children(h))
                {
                    if (c == self || IsHub(c)) continue;
                    var name = DevNode.Name(c);
                    if (name.Length > 0 && !peers.Contains(name, StringComparer.OrdinalIgnoreCase)) peers.Add(name);
                }
            return (true, peers);
        }
        catch (Exception ex) { Logger.Debug("usb peers: " + ex.Message); return (false, peers); }
    }

    /// <summary>下级集线器本身不算“设备”（驱动是 usbhub / USBHUB3）。</summary>
    private static bool IsHub(uint dev) => DevNode.StringProp(dev, DevNode.Service).StartsWith("usbhub", StringComparison.OrdinalIgnoreCase);

    private static string? Vid(string deviceId)
    {
        int i = deviceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && deviceId.Length >= i + 8 ? deviceId.Substring(i + 4, 4).ToUpperInvariant() : null;
    }
}

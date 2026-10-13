using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Wlan;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Beacon;

public sealed record BeaconInfo(int Channel, int FreqMHz, int WidthMHz, string Phy, int Streams)
{
    /// <summary>满血 = 5 GHz + ≥80 MHz + 802.11ax + ≥2 空间流</summary>
    public bool IsFull => WidthMHz >= 80 && Streams >= 2 && Phy == "802.11ax" && FreqMHz > 5000;
}

public sealed record BeaconResult(
    string Ssid, string Bssid, BeaconInfo Info, int Rssi,
    IReadOnlyList<string> SameChannelOthers, string ScannerName, DateTime Time)
{
    public string Summary =>
        Loc.T("beacon.channelMhzWidthMhzSpatial", Info.Channel, Info.FreqMHz, Info.WidthMHz, Info.Phy, Info.Streams, Rssi);
}

/// <summary>解析 802.11 信标里的信息元素（HT/VHT/HE），得出信道、带宽、协议、空间流。</summary>
public static class BeaconParser
{
    public static BeaconInfo Parse(byte[] ies, uint centerFreqKHz)
    {
        byte[]? htCap = null, htOp = null, vhtCap = null, vhtOp = null, heCap = null, heOp = null;
        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            var d = new byte[len];
            Array.Copy(ies, i + 2, d, 0, len);
            switch (id)
            {
                case 45: htCap = d; break;
                case 61: htOp = d; break;
                case 191: vhtCap = d; break;
                case 192: vhtOp = d; break;
                case 255:
                    if (len > 0 && d[0] == 35) heCap = d;
                    else if (len > 0 && d[0] == 36) heOp = d;
                    break;
            }
            i += 2 + len;
        }
        int mhz = (int)(centerFreqKHz / 1000);
        int width = 20;
        string phy = mhz > 4000 ? "802.11a" : "802.11g";
        if (htOp != null && htOp.Length >= 2)
        {
            phy = "802.11n";
            int off = htOp[1] & 3;
            if (off == 1 || off == 3) width = 40;
        }
        else if (htCap != null) phy = "802.11n";
        if (vhtOp != null && vhtOp.Length >= 3)
        {
            phy = "802.11ac";
            if (vhtOp[0] >= 1) width = vhtOp[2] != 0 ? 160 : 80;
        }
        else if (vhtCap != null) phy = "802.11ac";
        if (heOp != null || heCap != null) phy = "802.11ax";

        int streams = 0;
        if (heCap != null && heCap.Length >= 20) streams = CountNss(heCap[18] | (heCap[19] << 8));
        else if (vhtCap != null && vhtCap.Length >= 6) streams = CountNss(vhtCap[4] | (vhtCap[5] << 8));
        else if (htCap != null && htCap.Length >= 7)
            for (int n = 3; n <= 6; n++) if (htCap[n] != 0) streams++;

        int ch;
        if (htOp != null && htOp.Length >= 1) ch = htOp[0];
        else if (mhz > 5900) ch = (mhz - 5950) / 5;
        else if (mhz > 4000) ch = (mhz - 5000) / 5;
        else if (mhz == 2484) ch = 14;
        else ch = (mhz - 2407) / 5;
        return new BeaconInfo(ch, mhz, width, phy, streams);
    }

    /// <summary>MCS/NSS 映射：每 2 位一组，值 3 = 不支持该空间流。</summary>
    private static int CountNss(int map)
    {
        int n = 0;
        for (int k = 0; k < 8; k++) if (((map >> (2 * k)) & 3) != 3) n++;
        return n;
    }
}

/// <summary>
/// 用电脑上“另一块”无线网卡（通常是内置网卡）扫描热点的信标，核对实际射频参数。
/// </summary>
public static class BeaconChecker
{
    public static List<WlanInterface> EnumInterfaces() => WlanNative.EnumInterfaces();

    /// <summary>
    /// 用 D-Link 以外的无线网卡扫描 GO MAC 对应的信标。没有第二块无线网卡、或扫不到时返回 null 并给出原因。
    /// 扫描结果同时交给 <see cref="ChannelInfo"/>（信道面板）。
    /// </summary>
    public static async Task<(BeaconResult? result, string? reason)> CheckAsync(string goMac, string? dongleGuid, CancellationToken ct = default)
    {
        var ifs = EnumInterfaces();
        Guid dg = Guid.Empty;
        if (!string.IsNullOrEmpty(dongleGuid)) Guid.TryParse(dongleGuid, out dg);
        var scanner = ifs.FirstOrDefault(i => i.Guid != dg && !i.Description.Contains("DWA-F18", StringComparison.OrdinalIgnoreCase));
        if (scanner == null)
            return (null, Loc.T("beacon.pcHasNoSecondWi"));
        var target = MacUtil.Normalize(goMac);
        Logger.Info(Loc.T("beacon.scanningHotspotBeaconWithCheck", scanner.Description));
        WlanBss? hit = null;
        List<WlanBss> all = new();
        for (int attempt = 0; attempt < 3 && hit == null; attempt++)
        {
            try { WlanNative.Scan(scanner.Guid); } catch { }
            await Task.Delay(4500, ct);
            try { all = WlanNative.GetBssList(scanner.Guid); }
            catch (Exception ex) { return (null, Loc.T("beacon.failedReadScanResults") + ex.Message); }
            hit = all.FirstOrDefault(b => MacUtil.Normalize(b.Bssid) == target);
        }
        try { ChannelInfo.Record(all, scanner.Description, goMac, dongleGuid); } catch (Exception ex) { Logger.Debug(Loc.T("beacon.failedRecordChannelScanResults") + ex.Message); }
        if (hit == null) return (null, Loc.T("beacon.scanDidnTSeeHotspot"));
        var info = BeaconParser.Parse(hit.Ies, hit.FreqKHz);
        var same = all.Where(b => MacUtil.Normalize(b.Bssid) != target && (int)(b.FreqKHz / 1000) == info.FreqMHz)
                      .Select(b => string.IsNullOrEmpty(b.Ssid) ? b.Bssid : b.Ssid)
                      .Distinct().ToList();
        return (new BeaconResult(hit.Ssid, hit.Bssid, info, hit.Rssi, same, scanner.Description, DateTime.Now), null);
    }
}

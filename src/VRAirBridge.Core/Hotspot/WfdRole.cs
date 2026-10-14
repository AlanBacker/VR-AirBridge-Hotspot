using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.SystemSetup;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Hotspot;

/// <summary>一块物理无线网卡（不含 Wi-Fi Direct 虚拟网卡）。State 用 MSFT_NetAdapter 语义，3 = 已禁用。</summary>
public sealed record WlanAdapter(string Name, string Description, string Guid, string Mac, string PnpId, int State, int MediaConnectState)
{
    public bool IsDisabled => State == 3;
    public bool IsConnected => MediaConnectState == 1;
}

/// <summary>把 Wi-Fi Direct 挪到 D-Link 的结果。</summary>
public sealed record WfdMoveResult(bool Ok, bool WentOffline, double Seconds, IReadOnlyList<string> Paused);

/// <summary>
/// 热点只能开在 Wi-Fi Direct 虚拟网卡上，而 Windows 只把 Wi-Fi Direct 交给一块物理无线网卡，每次开机给谁不固定。
/// 交给了别的网卡（例如内置网卡）以后，重插 D-Link 也抢不回来；只有那块网卡暂时不在、D-Link 重新上线时才会拿到，
/// 之后再启用那块网卡，它也不会抢回去（2026-10-13 在作者电脑上实测）。
/// 判断谁拿着：看设备树里物理网卡下面有没有已启动的 Wi-Fi Direct 子设备（VWIFIMP_WFD）。
/// 不能用 NetworkInterface 列表判断：里面有过滤驱动的副本、已拔掉的旧条目，被停用的也显示为 Down。
/// </summary>
public static class WfdRole
{
    private static readonly ManagementScope StdCimv2 = new(@"\\.\root\StandardCimv2");

    /// <summary>这块物理网卡下面已启动的 Wi-Fi Direct 虚拟网卡数（0 = Windows 没把 Wi-Fi Direct 给它，热点开不到它上面）。</summary>
    public static int WfdPorts(string? pnpId)
    {
        try
        {
            if (DevNode.Locate(pnpId) is not uint dev) return 0;
            return DevNode.Children(dev).Count(c => DevNode.Id(c).Contains("VWIFIMP_WFD", StringComparison.OrdinalIgnoreCase) && DevNode.IsStarted(c));
        }
        catch { return 0; }
    }

    /// <summary>D-Link 有没有 Wi-Fi Direct（有 = Windows 会把热点开在它上面）。</summary>
    public static bool DongleHasWfd(DongleInfo dongle) => WfdPorts(dongle.PnpId) > 0;

    /// <summary>所有物理无线网卡（原生 802.11、硬件网卡），包括 D-Link。</summary>
    public static List<WlanAdapter> WirelessAdapters()
    {
        var list = new List<WlanAdapter>();
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery(
                "SELECT Name, InterfaceDescription, InterfaceGuid, PermanentAddress, PnPDeviceID, State, InterfaceAdminStatus, MediaConnectState, NdisPhysicalMedium, HardwareInterface FROM MSFT_NetAdapter"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                {
                    if (Convert.ToInt32(o["NdisPhysicalMedium"] ?? 0) != 9) continue;
                    if (o["HardwareInterface"] is bool hw && !hw) continue;
                    list.Add(new WlanAdapter(o["Name"] as string ?? "", o["InterfaceDescription"] as string ?? "", o["InterfaceGuid"] as string ?? "",
                        MacUtil.Format(o["PermanentAddress"] as string), o["PnPDeviceID"] as string ?? "", DongleInfo.EffectiveState(o), Convert.ToInt32(o["MediaConnectState"] ?? 0)));
                }
        }
        catch { }
        return list;
    }

    /// <summary>D-Link 以外、正在使用中的物理无线网卡。</summary>
    public static List<WlanAdapter> OtherAdapters(string dongleMac) =>
        WirelessAdapters().Where(a => !MacUtil.SameRadio(a.Mac, dongleMac) && !a.IsDisabled).ToList();

    /// <summary>拿着 Wi-Fi Direct 的其它网卡；一块都看不出来（比如它的 Wi-Fi Direct 子设备被停用了）时返回全部其它网卡。</summary>
    public static List<WlanAdapter> AdaptersToPause(IReadOnlyList<WlanAdapter> others, Func<WlanAdapter, int> wfdPorts)
    {
        var hit = others.Where(o => wfdPorts(o) > 0).ToList();
        return hit.Count > 0 ? hit : others.ToList();
    }

    /// <summary>
    /// 把 Wi-Fi Direct 挪到 D-Link（需要管理员；调用前先停掉热点）：
    /// 暂时停用拿着它的网卡 → 重启 D-Link → 等 D-Link 拿到 → 一定把停用的网卡启用回来，并等它重新连上网。
    /// 停用的网卡正在上网时，电脑会断网十几秒；<paramref name="busy"/> 用来在界面上说明正在做什么。
    /// </summary>
    public static async Task<WfdMoveResult> MoveToDongleAsync(DongleInfo dongle, Action<string> busy, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var pause = AdaptersToPause(OtherAdapters(dongle.Mac), a => WfdPorts(a.PnpId));
        bool offline = pause.Any(a => a.IsConnected);
        busy(offline ? Loc.T("wfd.busyOffline") : Loc.T("wfd.busy"));
        var paused = new List<WlanAdapter>();
        try
        {
            foreach (var a in pause)
            {
                Logger.Info(Loc.T("wfd.pausing", a.Name, a.Description));
                if (SetEnabled(a.Guid, a.Name, false)) paused.Add(a);
            }
            if (paused.Count > 0) await Task.Delay(1500, CancellationToken.None);

            Logger.Info(Loc.T("wfd.restartingDongle"));
            SetEnabled(dongle.Guid, dongle.Name, false);
            await Task.Delay(3000, CancellationToken.None);
            SetEnabled(dongle.Guid, dongle.Name, true);
            for (int i = 0; i < 25 && !DongleHasWfd(dongle); i++)
                await Task.Delay(1000, CancellationToken.None);
        }
        catch (Exception ex) { Logger.Warn(Loc.T("wfd.error") + ex.Message); }
        finally
        {
            // 无论成败都要把停用的网卡启用回来，不能让电脑一直断网
            foreach (var a in paused)
            {
                Logger.Info(Loc.T("wfd.resuming", a.Name));
                SetEnabled(a.Guid, a.Name, true);
            }
        }
        bool ok = DongleHasWfd(dongle);
        if (paused.Any(a => a.IsConnected)) await WaitOnlineAsync(paused.Where(a => a.IsConnected).ToList(), ct);
        sw.Stop();
        return new WfdMoveResult(ok, offline && paused.Count > 0, sw.Elapsed.TotalSeconds, paused.Select(a => a.Name).ToList());
    }

    /// <summary>等启用回来的网卡重新连上网（有 IPv4 网关），最多 45 秒。</summary>
    private static async Task WaitOnlineAsync(List<WlanAdapter> adapters, CancellationToken ct)
    {
        var want = adapters.Select(a => a.Guid.Trim('{', '}')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(45))
        {
            try
            {
                var back = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => want.Contains(n.Id.Trim('{', '}')) && n.OperationalStatus == OperationalStatus.Up)
                    .Any(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                if (back)
                {
                    Logger.Ok(Loc.T("wfd.backOnline", string.Join(", ", adapters.Select(a => a.Name)), sw.Elapsed.TotalSeconds.ToString("0")));
                    return;
                }
            }
            catch { }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
        Logger.Warn(Loc.T("wfd.notBackOnline", string.Join(", ", adapters.Select(a => a.Name))));
    }

    /// <summary>启用 / 停用网卡：先用 WMI（快），失败再用 PowerShell 的 Enable/Disable-NetAdapter。</summary>
    private static bool SetEnabled(string guid, string name, bool enable)
    {
        try
        {
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery("SELECT * FROM MSFT_NetAdapter"));
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (!string.Equals(o["InterfaceGuid"] as string, guid, StringComparison.OrdinalIgnoreCase)) continue;
                    var rc = o.InvokeMethod(enable ? "Enable" : "Disable", null);
                    if (Convert.ToUInt32(rc ?? 1u) == 0) return true;
                    break;
                }
        }
        catch (Exception ex) { Logger.Debug("MSFT_NetAdapter." + (enable ? "Enable" : "Disable") + ": " + ex.Message); }
        return IcsRepair.SetAdapterEnabled(name, enable);
    }
}

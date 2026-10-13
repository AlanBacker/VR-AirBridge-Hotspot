using System.Management;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Hotspot;

/// <summary>D-Link DWA-F18 "VR Air Bridge"（Realtek RTL8852AU）网卡的信息。</summary>
public sealed record DongleInfo(
    string Name,
    string Description,
    string Mac,
    int IfIndex,
    string Guid,
    string PnpId,
    string DriverVersion,
    string DriverDate,
    string DriverProvider,
    int State,
    int MediaConnectState)
{
    public const string HardwareIdPrefix = @"USB\VID_2001&PID_3323";
    public const string DescriptionKeyword = "DWA-F18";

    /// <summary>MSFT_NetAdapter.State: 3 = 已禁用（见 EffectiveState）</summary>
    public bool IsDisabled => State == 3;

    /// <summary>
    /// 网卡状态（MSFT_NetAdapter.State 语义，3 = 已禁用）。实测被禁用的 Hyper-V 虚拟网卡 State 仍是 1，
    /// 只有 InterfaceAdminStatus = 2（管理性关闭）能看出来，所以两者合并判断。查询里必须包含这两个字段。
    /// </summary>
    public static int EffectiveState(ManagementBaseObject o)
    {
        int state = Convert.ToInt32(o["State"] ?? 0);
        int admin = 1;
        try { admin = Convert.ToInt32(o["InterfaceAdminStatus"] ?? 1); } catch { }
        return admin == 2 ? 3 : state;
    }

    public string StatusText => IsDisabled ? Loc.T("dongle.disabled")
        : MediaConnectState == 1 ? Loc.T("dongle.connectedAsClientAnotherWi")
        : Loc.T("dongle.ready");

    public string DriverText => string.IsNullOrEmpty(DriverDate) ? DriverVersion : $"{DriverVersion} ({DriverDate})";

    /// <summary>无线模式（驱动高级属性 WifiProtocol_phy0），例如 802.11a/b/g/n/ac/ax；读不到时为空。</summary>
    public string? WirelessMode { get; init; }

    private static readonly ManagementScope StdCimv2 = new(@"\\.\root\StandardCimv2");
    private static string? _cachedModeKey;
    private static string? _cachedMode;

    /// <summary>查找 D-Link 网卡：先按硬件 ID（USB\VID_2001&amp;PID_3323），再按描述里的 “DWA-F18”。</summary>
    public static DongleInfo? Find()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StdCimv2, new ObjectQuery(
                "SELECT Name, InterfaceDescription, PermanentAddress, InterfaceIndex, InterfaceGuid, PnPDeviceID, " +
                "DriverVersionString, DriverDate, DriverProvider, State, InterfaceAdminStatus, MediaConnectState FROM MSFT_NetAdapter"));
            ManagementBaseObject? pick = null;
            var all = searcher.Get().Cast<ManagementBaseObject>().ToList();
            pick = all.FirstOrDefault(o => (o["PnPDeviceID"] as string ?? "").StartsWith(HardwareIdPrefix, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(o => (o["InterfaceDescription"] as string ?? "").Contains(DescriptionKeyword, StringComparison.OrdinalIgnoreCase));
            if (pick == null) return null;
            var info = new DongleInfo(
                Name: pick["Name"] as string ?? "",
                Description: pick["InterfaceDescription"] as string ?? "",
                Mac: MacUtil.Format(pick["PermanentAddress"] as string),
                IfIndex: Convert.ToInt32(pick["InterfaceIndex"] ?? -1),
                Guid: pick["InterfaceGuid"] as string ?? "",
                PnpId: pick["PnPDeviceID"] as string ?? "",
                DriverVersion: pick["DriverVersionString"] as string ?? "",
                DriverDate: pick["DriverDate"] as string ?? "",
                DriverProvider: pick["DriverProvider"] as string ?? "",
                State: EffectiveState(pick),
                MediaConnectState: Convert.ToInt32(pick["MediaConnectState"] ?? 0));
            foreach (var o in all) o.Dispose();
            return info with { WirelessMode = GetWirelessMode(info) };
        }
        catch (Exception ex)
        {
            Logger.Debug(Loc.T("dongle.failedQueryDLinkAdapter") + ex.Message);
            return null;
        }
    }

    private static long _usbProbeAt = long.MinValue / 2;
    private static string? _usbProbe;

    /// <summary>
    /// D-Link 硬件插着、但没有可用的网卡驱动（所以 MSFT_NetAdapter 里找不到）时返回设备管理器里的设备名，否则 null。
    /// 用来区分“没插网卡”和“插了但没装驱动”（例如卸载过 Meta 软件、或从没装过驱动的电脑）。结果缓存 15 秒。
    /// </summary>
    public static string? FindUsbWithoutDriver()
    {
        var now = Environment.TickCount64;
        if (now - _usbProbeAt < 15000) return _usbProbe;
        _usbProbeAt = now;
        _usbProbe = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                // WQL 字符串里反斜杠要写成 \\（这里是逐字字符串，原样传给 WMI）
                @"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID_2001&PID_3323%'");
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                {
                    if (_usbProbe != null) continue;
                    var name = o["Name"] as string;
                    _usbProbe = string.IsNullOrWhiteSpace(name) ? HardwareIdPrefix : name;
                }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("dongle.failedQueryUsbDevices") + ex.Message); }
        return _usbProbe;
    }

    public static string NoDriverHint => SystemSetup.DriverStrings.NoDriverHint;

    /// <summary>让下一次 <see cref="FindUsbWithoutDriver"/> 重新查询（装完驱动后调用）。</summary>
    public static void InvalidateUsbProbe() => _usbProbeAt = long.MinValue / 2;

    private static string? GetWirelessMode(DongleInfo d)
    {
        var key = d.Guid + "|" + d.DriverVersion;
        if (_cachedModeKey == key) return _cachedMode;
        string? mode = null;
        try
        {
            var desc = d.Description.Replace("'", "''");
            using var s = new ManagementObjectSearcher(StdCimv2, new ObjectQuery(
                $"SELECT DisplayValue, RegistryKeyword FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE InterfaceDescription = '{desc}'"));
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    if (string.Equals(o["RegistryKeyword"] as string, "WifiProtocol_phy0", StringComparison.OrdinalIgnoreCase))
                        mode = o["DisplayValue"] as string;
                }
            }
        }
        catch { }
        _cachedModeKey = key;
        _cachedMode = mode;
        return mode;
    }

    /// <summary>启用被禁用的网卡（需要管理员）。</summary>
    public static bool TryEnable(DongleInfo d)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StdCimv2, new ObjectQuery("SELECT * FROM MSFT_NetAdapter"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (string.Equals(o["InterfaceGuid"] as string, d.Guid, StringComparison.OrdinalIgnoreCase))
                    {
                        o.InvokeMethod("Enable", null);
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) { Logger.Warn(Loc.T("dongle.failedEnableDLinkAdapter") + ex.Message); }
        return false;
    }
}

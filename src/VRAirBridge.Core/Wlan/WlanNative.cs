using System.Runtime.InteropServices;
using System.Text;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Wlan;

/// <summary>wlanapi 里的一块无线网卡（State：1 = 已连接，4 = 未连接 …）。</summary>
public sealed record WlanInterface(Guid Guid, string Description, int State)
{
    public bool Connected => State == 1;
}

/// <summary>扫描到的一个 BSS（FreqKHz = 中心频率，单位 kHz）。</summary>
public sealed record WlanBss(string Ssid, string Bssid, int Rssi, uint FreqKHz, byte[] Ies);

/// <summary>网卡当前的 STA 连接。</summary>
public sealed record WlanConnection(int State, string ProfileName, string Ssid, string Bssid, int SignalQuality);

public sealed class WlanException : Exception
{
    public uint Code { get; }
    public WlanException(string op, uint code) : base(Loc.T("wlan.returned", op, code)) => Code = code;
}

/// <summary>
/// wlanapi.dll 的 P/Invoke 封装（扫描、BSS 列表、当前连接、配置文件、连接/断开）。
/// 注意：配置文件 XML 里可能有 Wi-Fi 密码，调用方只能放在内存里，不能记日志、不能写盘。
/// </summary>
internal static class WlanNative
{
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool secure, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, out int opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr handle, ref Guid iface, string profileName, IntPtr reserved, out IntPtr xml, ref uint flags, out uint grantedAccess);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string xml, string? allUserSecurity, bool overwrite, IntPtr reserved, out uint reasonCode);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanDeleteProfile(IntPtr handle, ref Guid iface, string profileName, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr handle, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);

    public const uint ProfileGetPlaintextKey = 4;
    private const int OpInterfaceState = 6, OpCurrentConnection = 7, OpChannelNumber = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID { public uint len; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ssid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_RATE_SET { public uint len; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] rates; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID ssid; public uint phyId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] bssid;
        public int bssType; public int phyType; public int rssi; public uint linkQuality; public byte inRegDomain; public ushort beaconPeriod;
        public ulong timestamp; public ulong hostTimestamp; public ushort capInfo; public uint centerFreq; public WLAN_RATE_SET rateSet; public uint ieOffset; public uint ieSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int mode;                 // 0 = wlan_connection_mode_profile
        [MarshalAs(UnmanagedType.LPWStr)] public string profile;
        public IntPtr ssid;
        public IntPtr bssidList;
        public int bssType;              // 1 = infrastructure
        public uint flags;
    }

    private static IntPtr Open()
    {
        var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var h);
        if (rc != 0) throw new WlanException("WlanOpenHandle", rc);
        return h;
    }

    private static T With<T>(Func<IntPtr, T> body)
    {
        var h = Open();
        try { return body(h); }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    public static List<WlanInterface> EnumInterfaces()
    {
        try
        {
            return With(h =>
            {
                var res = new List<WlanInterface>();
                if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return res;
                try
                {
                    int n = Marshal.ReadInt32(list);
                    const int itemSize = 16 + 512 + 4;
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr p = list + 8 + i * itemSize;
                        var gb = new byte[16];
                        Marshal.Copy(p, gb, 0, 16);
                        string desc = Marshal.PtrToStringUni(p + 16, 256).TrimEnd('\0');
                        int state = Marshal.ReadInt32(p + 16 + 512);
                        res.Add(new WlanInterface(new Guid(gb), desc, state));
                    }
                }
                finally { WlanFreeMemory(list); }
                return res;
            });
        }
        catch (WlanException) { return new List<WlanInterface>(); }
        catch (DllNotFoundException) { return new List<WlanInterface>(); }
    }

    public static void Scan(Guid iface) => With(h => WlanScan(h, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    public static List<WlanBss> GetBssList(Guid iface) => With(h =>
    {
        var res = new List<WlanBss>();
        uint rc = WlanGetNetworkBssList(h, ref iface, IntPtr.Zero, 3, false, IntPtr.Zero, out var list);
        if (rc != 0) throw new WlanException("WlanGetNetworkBssList", rc);
        try
        {
            int count = Marshal.ReadInt32(list, 4);
            int esz = Marshal.SizeOf<WLAN_BSS_ENTRY>();
            for (int i = 0; i < count; i++)
            {
                IntPtr ep = list + 8 + i * esz;
                var e = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(ep);
                var ies = new byte[e.ieSize];
                Marshal.Copy(ep + (int)e.ieOffset, ies, 0, (int)e.ieSize);
                int slen = (int)Math.Min(e.ssid.len, 32);
                res.Add(new WlanBss(Encoding.UTF8.GetString(e.ssid.ssid, 0, slen), MacUtil.Format(e.bssid), e.rssi, e.centerFreq, ies));
            }
        }
        finally { WlanFreeMemory(list); }
        return res;
    });

    /// <summary>当前连接（没连接时返回 null）。</summary>
    public static WlanConnection? QueryConnection(Guid iface)
    {
        try
        {
            return With(h =>
            {
                if (WlanQueryInterface(h, ref iface, OpCurrentConnection, IntPtr.Zero, out var size, out var data, out _) != 0) return null;
                try
                {
                    if (size < 580) return null;
                    int state = Marshal.ReadInt32(data);
                    string profile = Marshal.PtrToStringUni(data + 8, 256).TrimEnd('\0');
                    int slen = Math.Min(Marshal.ReadInt32(data + 520), 32);
                    var sb = new byte[Math.Max(slen, 0)];
                    if (slen > 0) Marshal.Copy(data + 524, sb, 0, slen);
                    var mac = new byte[6];
                    Marshal.Copy(data + 560, mac, 0, 6);
                    int quality = Marshal.ReadInt32(data + 576);
                    return new WlanConnection(state, profile, Encoding.UTF8.GetString(sb), MacUtil.Format(mac), quality);
                }
                finally { WlanFreeMemory(data); }
            });
        }
        catch (WlanException) { return null; }
    }

    /// <summary>网卡当前所在信道（没连接时返回 null）。</summary>
    public static int? QueryChannel(Guid iface)
    {
        try
        {
            return With<int?>(h =>
            {
                if (WlanQueryInterface(h, ref iface, OpChannelNumber, IntPtr.Zero, out var size, out var data, out _) != 0) return null;
                try { return size >= 4 ? Marshal.ReadInt32(data) : null; }
                finally { WlanFreeMemory(data); }
            });
        }
        catch (WlanException) { return null; }
    }

    public static int? QueryState(Guid iface)
    {
        try
        {
            return With<int?>(h =>
            {
                if (WlanQueryInterface(h, ref iface, OpInterfaceState, IntPtr.Zero, out var size, out var data, out _) != 0) return null;
                try { return size >= 4 ? Marshal.ReadInt32(data) : null; }
                finally { WlanFreeMemory(data); }
            });
        }
        catch (WlanException) { return null; }
    }

    public static List<string> GetProfileNames(Guid iface)
    {
        try
        {
            return With(h =>
            {
                var res = new List<string>();
                if (WlanGetProfileList(h, ref iface, IntPtr.Zero, out var list) != 0) return res;
                try
                {
                    int n = Marshal.ReadInt32(list);
                    const int itemSize = 512 + 4;
                    for (int i = 0; i < n; i++)
                        res.Add(Marshal.PtrToStringUni(list + 8 + i * itemSize, 256).TrimEnd('\0'));
                }
                finally { WlanFreeMemory(list); }
                return res;
            });
        }
        catch (WlanException) { return new List<string>(); }
    }

    /// <summary>读取配置文件 XML。plaintextKey = true 时密钥是明文（需要管理员）—— 结果只能留在内存里。</summary>
    public static string? GetProfileXml(Guid iface, string profileName, bool plaintextKey)
    {
        return With(h =>
        {
            uint flags = plaintextKey ? ProfileGetPlaintextKey : 0;
            var rc = WlanGetProfile(h, ref iface, profileName, IntPtr.Zero, out var xml, ref flags, out _);
            if (rc != 0) return null;
            try { return Marshal.PtrToStringUni(xml); }
            finally { WlanFreeMemory(xml); }
        });
    }

    /// <summary>写入（覆盖）全体用户配置文件。失败抛 WlanException（不带 XML 内容）。</summary>
    public static void SetProfileXml(Guid iface, string xml)
    {
        With(h =>
        {
            var rc = WlanSetProfile(h, ref iface, 0, xml, null, true, IntPtr.Zero, out var reason);
            if (rc != 0) throw new WlanException(Loc.T("wlan.wlansetprofileReasonCode", reason), rc);
            return 0;
        });
    }

    public static bool DeleteProfile(Guid iface, string profileName)
    {
        try { return With(h => WlanDeleteProfile(h, ref iface, profileName, IntPtr.Zero) == 0); }
        catch (WlanException) { return false; }
    }

    public static void Connect(Guid iface, string profileName)
    {
        With(h =>
        {
            var p = new WLAN_CONNECTION_PARAMETERS { mode = 0, profile = profileName, ssid = IntPtr.Zero, bssidList = IntPtr.Zero, bssType = 1, flags = 0 };
            var rc = WlanConnect(h, ref iface, ref p, IntPtr.Zero);
            if (rc != 0) throw new WlanException("WlanConnect", rc);
            return 0;
        });
    }

    public static bool Disconnect(Guid iface)
    {
        try { return With(h => WlanDisconnect(h, ref iface, IntPtr.Zero) == 0); }
        catch (WlanException) { return false; }
    }
}

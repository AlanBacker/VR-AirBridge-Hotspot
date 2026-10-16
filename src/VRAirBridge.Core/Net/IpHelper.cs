using System.Net;
using System.Runtime.InteropServices;

namespace VRAirBridge.Core.Net;

public sealed record UdpListener(IPAddress Address, int Port, int Pid);

public sealed record ArpEntry(int InterfaceIndex, IPAddress Address, string Mac, int Type);

/// <summary>NL_NEIGHBOR_STATE (GetIpNetTable2).</summary>
public enum NeighborState { Unreachable = 0, Incomplete = 1, Probe = 2, Delay = 3, Stale = 4, Reachable = 5, Permanent = 6 }

/// <summary>One IPv4 neighbor (ARP) entry from GetIpNetTable2. Mac is "AA-BB-CC-DD-EE-FF".</summary>
public sealed record NeighborEntry(int InterfaceIndex, IPAddress Address, string Mac, NeighborState State)
{
    /// <summary>Confirmed recently (Probe / Delay / Reachable) or static.</summary>
    public bool IsFresh => State is NeighborState.Probe or NeighborState.Delay or NeighborState.Reachable or NeighborState.Permanent;
}

/// <summary>iphlpapi 封装：带进程号的 UDP 监听表（netstat -ano 等价）、ARP 表。</summary>
public static class IpHelper
{
    private const int AF_INET = 2;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    /// <summary>
    /// IPv4 neighbor table (GetIpNetTable2) with the neighbor state, optionally only one interface.
    /// Unicast entries with a 6-byte MAC only (multicast / broadcast / incomplete entries are skipped).
    /// </summary>
    public static List<NeighborEntry> GetNeighbors(int? interfaceIndex = null)
    {
        var result = new List<NeighborEntry>();
        if (GetIpNetTable2(AF_INET, out var table) != 0 || table == IntPtr.Zero) return result;
        try
        {
            int n = Marshal.ReadInt32(table);
            // MIB_IPNET_TABLE2: ULONG NumEntries, then (8-byte aligned) MIB_IPNET_ROW2[NumEntries], 88 bytes each:
            // SOCKADDR_INET Address (28: family@0, IPv4@4) | InterfaceIndex@28 | Luid@32 | PhysicalAddress[32]@40 |
            // PhysicalAddressLength@72 | State@76 | Flags@80 | ReachabilityTime@84
            const int rowSize = 88;
            IntPtr row = table + 8;
            var phys = new byte[6];
            for (int i = 0; i < n; i++, row += rowSize)
            {
                if ((ushort)Marshal.ReadInt16(row) != AF_INET) continue;
                int ifIndex = Marshal.ReadInt32(row + 28);
                if (interfaceIndex != null && ifIndex != interfaceIndex) continue;
                int physLen = Marshal.ReadInt32(row + 72);
                if (physLen != 6) continue;
                Marshal.Copy(row + 40, phys, 0, 6);
                if (phys.All(b => b == 0) || phys.All(b => b == 0xFF) || (phys[0] & 1) != 0) continue;
                var addrBytes = new byte[4];
                Marshal.Copy(row + 4, addrBytes, 0, 4);
                if (addrBytes[0] >= 224 || addrBytes[3] == 255 || addrBytes[0] == 0) continue;
                var state = (NeighborState)Marshal.ReadInt32(row + 76);
                result.Add(new NeighborEntry(ifIndex, new IPAddress(addrBytes), string.Join("-", phys.Select(b => b.ToString("X2"))), state));
            }
        }
        finally { FreeMibTable(table); }
        return result;
    }

    /// <summary>所有 IPv4 UDP 监听端点及其所属进程号。</summary>
    public static List<UdpListener> GetUdpListeners()
    {
        var result = new List<UdpListener>();
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int sz = size + 1024;
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                uint rc = GetExtendedUdpTable(buf, ref sz, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (rc == ERROR_INSUFFICIENT_BUFFER) { size = sz; continue; }
                if (rc != 0) return result;
                int n = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                for (int i = 0; i < n; i++)
                {
                    uint addr = (uint)Marshal.ReadInt32(row);
                    uint portRaw = (uint)Marshal.ReadInt32(row + 4);
                    int pid = Marshal.ReadInt32(row + 8);
                    int port = (int)(((portRaw & 0xFF) << 8) | ((portRaw >> 8) & 0xFF));
                    result.Add(new UdpListener(new IPAddress(addr), port, pid));
                    row += 12;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return result;
    }

    /// <summary>IPv4 ARP（邻居）表。Type: 2=无效 3=动态 4=静态。</summary>
    public static List<ArpEntry> GetArpTable()
    {
        var result = new List<ArpEntry>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return result;
        int sz = size + 4096;
        IntPtr buf = Marshal.AllocHGlobal(sz);
        try
        {
            if (GetIpNetTable(buf, ref sz, false) != 0) return result;
            int n = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            for (int i = 0; i < n; i++)
            {
                int index = Marshal.ReadInt32(row);
                int physLen = Marshal.ReadInt32(row + 4);
                var phys = new byte[8];
                Marshal.Copy(row + 8, phys, 0, 8);
                uint addr = (uint)Marshal.ReadInt32(row + 16);
                int type = Marshal.ReadInt32(row + 20);
                if (physLen > 8) physLen = 8;
                result.Add(new ArpEntry(index, new IPAddress(addr), string.Join("-", phys.Take(physLen).Select(b => b.ToString("X2"))), type));
                row += 24;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    /// <summary>
    /// IPv4 默认路由（0.0.0.0/0）在每个接口上的最小有效跃点数，key = 接口索引。
    /// Vista 以后 MIB_IPFORWARDROW.dwForwardMetric1 = 路由跃点 + 接口跃点，也就是 Windows 实际选路用的值。
    /// </summary>
    public static Dictionary<int, int> GetDefaultRouteMetrics() => DefaultRouteMetrics(GetIPv4Routes());

    /// <summary>The 0.0.0.0/0 routes of a route table: lowest effective metric per interface index.</summary>
    public static Dictionary<int, int> DefaultRouteMetrics(IEnumerable<RouteRow> routes)
    {
        var result = new Dictionary<int, int>();
        foreach (var r in routes)
            if (r.Destination == 0 && r.Mask == 0 && r.IfIndex > 0 && r.Metric >= 0)
                result[r.IfIndex] = result.TryGetValue(r.IfIndex, out var m) ? Math.Min(m, r.Metric) : r.Metric;
        return result;
    }

    /// <summary>The IPv4 route table (GetIpForwardTable). Empty when it cannot be read.</summary>
    public static List<RouteRow> GetIPv4Routes()
    {
        var result = new List<RouteRow>();
        int size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int sz = size + 4096;
            IntPtr buf = Marshal.AllocHGlobal(sz);
            try
            {
                uint rc = GetIpForwardTable(buf, ref sz, false);
                if (rc == ERROR_INSUFFICIENT_BUFFER) { size = sz; continue; }
                if (rc != 0) return result;
                int n = Marshal.ReadInt32(buf);
                IntPtr row = buf + 4;
                // MIB_IPFORWARDROW: 14 个 DWORD = Dest, Mask, Policy, NextHop, IfIndex, Type, Proto, Age, NextHopAS, Metric1..5
                for (int i = 0; i < n; i++)
                {
                    // Dest and Mask are in network byte order
                    uint dest = (uint)IPAddress.NetworkToHostOrder(Marshal.ReadInt32(row));
                    uint mask = (uint)IPAddress.NetworkToHostOrder(Marshal.ReadInt32(row + 4));
                    int ifIndex = Marshal.ReadInt32(row + 16);
                    int metric = Marshal.ReadInt32(row + 36);
                    result.Add(new RouteRow(dest, mask, ifIndex, metric));
                    row += 56;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return result;
    }
}

/// <summary>One IPv4 route. Destination and Mask in host byte order (128.0.0.0 = 0x80000000); Metric = route + interface metric.</summary>
public sealed record RouteRow(uint Destination, uint Mask, int IfIndex, int Metric)
{
    public static RouteRow Of(string prefix, int ifIndex, int metric)
    {
        var parts = prefix.Split('/');
        var len = int.Parse(parts[1]);
        uint mask = len == 0 ? 0 : uint.MaxValue << (32 - len);
        return new RouteRow(NetworkInfo.ToUInt32(IPAddress.Parse(parts[0])), mask, ifIndex, metric);
    }
}

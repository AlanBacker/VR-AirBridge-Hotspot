using System.Net;
using System.Runtime.InteropServices;

namespace VRAirBridge.Core.Net;

public sealed record UdpListener(IPAddress Address, int Port, int Pid);

public sealed record ArpEntry(int InterfaceIndex, IPAddress Address, string Mac, int Type);

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
}

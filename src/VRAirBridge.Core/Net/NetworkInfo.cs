using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Net;

/// <summary>热点（Wi-Fi Direct GO）虚拟网卡的信息。</summary>
public sealed record GoAdapterInfo(string Name, string Description, string Id, string Mac, int IfIndex, IPAddress? Ip, IPAddress? Mask)
{
    public bool HasUsableIp => Ip != null && !NetworkInfo.IsApipa(Ip);
}

/// <summary>上行（给热点供网的）网卡信息。</summary>
public sealed record UplinkInfo(string Name, string Description, string Id, int IfIndex, IPAddress? Ip, IPAddress? Gateway, IReadOnlyList<IPAddress> Dns);

public static class NetworkInfo
{
    public const string WifiDirectPrefix = "Microsoft Wi-Fi Direct Virtual Adapter";

    public static bool IsApipa(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public static int GetIPv4Index(NetworkInterface ni)
    {
        try { return ni.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch { return -1; }
    }

    private static (IPAddress? ip, IPAddress? mask) FirstIPv4(NetworkInterface ni)
    {
        try
        {
            var addrs = ni.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            var pick = addrs.FirstOrDefault(a => !IsApipa(a.Address)) ?? addrs.FirstOrDefault();
            return pick == null ? (null, null) : (pick.Address, pick.IPv4Mask);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// 找到当前运行的热点虚拟网卡（Microsoft Wi-Fi Direct Virtual Adapter，状态 Up）。
    /// 如果有多个，优先选 MAC 和 D-Link 同族、且有可用 IP 的那个。
    /// </summary>
    public static GoAdapterInfo? FindGoAdapter(string? dongleMac)
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return null; }
        var cands = all
            .Where(n => n.Description.StartsWith(WifiDirectPrefix, StringComparison.OrdinalIgnoreCase)
                        && n.OperationalStatus == OperationalStatus.Up)
            .Select(n =>
            {
                var (ip, mask) = FirstIPv4(n);
                return new GoAdapterInfo(n.Name, n.Description, n.Id, MacUtil.Format(n.GetPhysicalAddress()), GetIPv4Index(n), ip, mask);
            }).ToList();
        if (cands.Count == 0) return null;
        return cands.OrderByDescending(c => MacUtil.SameRadio(c.Mac, dongleMac))
                    .ThenByDescending(c => c.HasUsableIp)
                    .First();
    }

    public static UplinkInfo? GetUplink(string? adapterId)
    {
        if (string.IsNullOrEmpty(adapterId)) return null;
        var want = adapterId.Trim().TrimStart('{').TrimEnd('}');
        NetworkInterface? ni = null;
        try
        {
            ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id.TrimStart('{').TrimEnd('}'), want, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (ni == null) return null;
        var (ip, _) = FirstIPv4(ni);
        IPAddress? gw = null;
        var dns = new List<IPAddress>();
        try
        {
            var props = ni.GetIPProperties();
            gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            dns.AddRange(props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        }
        catch { }
        return new UplinkInfo(ni.Name, ni.Description, ni.Id, GetIPv4Index(ni), ip, gw, dns);
    }

    /// <summary>根据 IP 反查本机网卡名。</summary>
    public static string? AdapterNameForIp(IPAddress ip)
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.Equals(ip)) return n.Name;
        }
        catch { }
        return null;
    }

    public static IPAddress? IPv4OfAdapter(string name)
    {
        try
        {
            var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Name == name);
            return n == null ? null : FirstIPv4(n).ip;
        }
        catch { return null; }
    }

    public static IPAddress NetworkAddress(IPAddress ip, IPAddress mask)
    {
        var a = ip.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var r = new byte[4];
        for (int i = 0; i < 4; i++) r[i] = (byte)(a[i] & m[i]);
        return new IPAddress(r);
    }

    public static int PrefixLength(IPAddress mask)
    {
        int bits = 0;
        foreach (var b in mask.GetAddressBytes())
            for (int i = 7; i >= 0; i--) if ((b & (1 << i)) != 0) bits++;
        return bits;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask) =>
        NetworkAddress(a, mask).Equals(NetworkAddress(b, mask));

    public static uint ToUInt32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt32(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}

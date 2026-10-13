using System.Net;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.Tests;

/// <summary>构造客户端 DHCP 报文的小工具（模拟 iPhone / Quest 等客户端）。</summary>
internal static class Pkt
{
    public static readonly IPAddress Server = IPAddress.Parse("192.168.137.1");
    public static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    public static byte[] Mac(int n) => new byte[] { 0x2C, 0x26, 0x17, 0x00, 0x00, (byte)n };
    public static string MacText(int n) => $"2C-26-17-00-00-{n:X2}";

    public static DhcpPacket Client(DhcpMessageType type, int macN, uint xid = 0x1234, string? host = null, bool broadcast = false)
    {
        var p = new DhcpPacket { Op = 1, HType = 1, HLen = 6, Xid = xid };
        Array.Copy(Mac(macN), p.ChAddr, 6);
        p.MessageType = type;
        p.SetOption(DhcpOption.ClientId, new byte[] { 1 }.Concat(Mac(macN)).ToArray());
        if (host != null) p.SetOption(DhcpOption.HostName, System.Text.Encoding.ASCII.GetBytes(host));
        p.SetOption(DhcpOption.ParameterRequestList, new byte[] { 1, 3, 6, 15, 51, 58, 59 });
        p.BroadcastFlag = broadcast;
        return p;
    }

    public static DhcpPacket Discover(int macN, string? host = null, IPAddress? requested = null)
    {
        var p = Client(DhcpMessageType.Discover, macN, host: host);
        if (requested != null) p.SetOption(DhcpOption.RequestedIp, requested);
        return p;
    }

    public static DhcpPacket Selecting(int macN, IPAddress ip, IPAddress? serverId = null, string? host = null)
    {
        var p = Client(DhcpMessageType.Request, macN, host: host);
        p.SetOption(DhcpOption.RequestedIp, ip);
        p.SetOption(DhcpOption.ServerId, serverId ?? Server);
        return p;
    }

    public static DhcpPacket InitReboot(int macN, IPAddress ip)
    {
        var p = Client(DhcpMessageType.Request, macN);
        p.SetOption(DhcpOption.RequestedIp, ip);
        return p;
    }

    public static DhcpPacket Renew(int macN, IPAddress ciaddr)
    {
        var p = Client(DhcpMessageType.Request, macN);
        p.CiAddr = ciaddr;
        return p;
    }

    /// <summary>经过“线路”：序列化再解析，保证测试的是真实字节。</summary>
    public static DhcpPacket Wire(DhcpPacket p) => DhcpPacket.Parse(p.ToBytes());
}

internal sealed class FakeClock
{
    public DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    public DateTime Get() => Now;
    public void Advance(TimeSpan t) => Now += t;
}

internal static class EngineFactory
{
    public static DhcpEngine Create(FakeClock clock, DhcpLeaseStore? store = null, string poolStart = "192.168.137.100", string poolEnd = "192.168.137.199")
    {
        var s = new DhcpSettings { PoolStart = poolStart, PoolEnd = poolEnd, LeaseHours = 12 };
        var cfg = DhcpEngineConfig.Create(Pkt.Server, Pkt.Mask, s, new[] { IPAddress.Parse("223.5.5.5"), IPAddress.Parse("8.8.8.8") });
        return new DhcpEngine(cfg, store ?? new DhcpLeaseStore(null), clock.Get) { Synchronous = true };
    }
}

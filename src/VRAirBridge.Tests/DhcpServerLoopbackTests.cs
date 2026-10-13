using System.Net;
using System.Net.Sockets;
using VRAirBridge.Core.Dhcp;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>
/// DHCP 收发测试：真实 UDP 套接字，服务器绑定 127.0.0.1 的高端口（不需要管理员，不碰真正的 67/68 端口），
/// “客户端”用手工构造的报文走完整的 DISCOVER → OFFER → REQUEST → ACK 流程。
/// </summary>
public class DhcpServerLoopbackTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private static async Task<DhcpPacket> ReceiveAsync(UdpClient c, uint xid, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var r = await c.ReceiveAsync(cts.Token);
            var p = DhcpPacket.Parse(r.Buffer);
            if (p.Op == 2 && p.Xid == xid) return p;
        }
    }

    [Fact]
    public async Task Loopback_FullHandshake_Then_Renew_And_Release()
    {
        int serverPort = FreeUdpPort(), clientPort = FreeUdpPort();
        var clock = new FakeClock();
        var engine = EngineFactory.Create(clock);
        using var server = new DhcpServer(engine, new DhcpServerOptions
        {
            ServerIp = IPAddress.Loopback,
            ServerPort = serverPort,
            ClientPort = clientPort,
            BindAny = false,
            InterfaceIndex = 0,
            BroadcastOverride = IPAddress.Loopback,
        });
        server.Start();
        Assert.True(server.IsRunning);
        Assert.Contains($"127.0.0.1:{serverPort}", server.BindText);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, clientPort));
        var serverEp = new IPEndPoint(IPAddress.Loopback, serverPort);

        // DISCOVER → OFFER
        var discover = Pkt.Discover(0x31, "iPhone-test");
        discover.Xid = 0xA1B2C3D4;
        discover.BroadcastFlag = true;
        await client.SendAsync(discover.ToBytes(), serverEp);
        var offer = await ReceiveAsync(client, 0xA1B2C3D4, TimeSpan.FromSeconds(5));
        Assert.Equal(DhcpMessageType.Offer, offer.MessageType);
        Assert.Equal(IPAddress.Parse("192.168.137.100"), offer.YiAddr);
        Assert.Equal(Pkt.Server, offer.GetIpOption(DhcpOption.ServerId));
        Assert.Equal(Pkt.Server, offer.GetIpOption(DhcpOption.Router));

        // REQUEST(SELECTING) → ACK
        var request = Pkt.Selecting(0x31, offer.YiAddr, host: "iPhone-test");
        request.Xid = 0xA1B2C3D5;
        await client.SendAsync(request.ToBytes(), serverEp);
        var ack = await ReceiveAsync(client, 0xA1B2C3D5, TimeSpan.FromSeconds(5));
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        Assert.Equal(offer.YiAddr, ack.YiAddr);
        Assert.Equal(43200u, ack.GetUInt32Option(DhcpOption.LeaseTime));

        // 租约已记录（主机名来自选项 12）
        var lease = engine.Store.GetByMac(Pkt.MacText(0x31));
        Assert.NotNull(lease);
        Assert.Equal("iPhone-test", lease!.HostName);

        // 错误网段的 INIT-REBOOT → NAK（广播）
        var bad = Pkt.InitReboot(0x32, IPAddress.Parse("10.9.8.7"));
        bad.Xid = 0xA1B2C3D6;
        await client.SendAsync(bad.ToBytes(), serverEp);
        var nak = await ReceiveAsync(client, 0xA1B2C3D6, TimeSpan.FromSeconds(5));
        Assert.Equal(DhcpMessageType.Nak, nak.MessageType);
        Assert.True(nak.BroadcastFlag);

        // RELEASE：不应答，租约被标记释放
        var release = Pkt.Client(DhcpMessageType.Release, 0x31);
        release.CiAddr = ack.YiAddr;
        release.SetOption(DhcpOption.ServerId, Pkt.Server);
        await client.SendAsync(release.ToBytes(), serverEp);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && engine.Store.GetByMac(Pkt.MacText(0x31))!.Released == false) await Task.Delay(50);
        Assert.True(engine.Store.GetByMac(Pkt.MacText(0x31))!.Released);

        Assert.True(server.Received >= 4);
        Assert.True(server.Sent >= 3);
        Assert.Equal(0, server.SendErrors);
        server.Stop();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Loopback_GarbageAndDuplicates_DoNotBreakServer()
    {
        int serverPort = FreeUdpPort(), clientPort = FreeUdpPort();
        var engine = EngineFactory.Create(new FakeClock());
        using var server = new DhcpServer(engine, new DhcpServerOptions
        {
            ServerIp = IPAddress.Loopback, ServerPort = serverPort, ClientPort = clientPort,
            BindAny = false, BroadcastOverride = IPAddress.Loopback,
        });
        server.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, clientPort));
        var ep = new IPEndPoint(IPAddress.Loopback, serverPort);
        await client.SendAsync(new byte[] { 1, 2, 3 }, ep);
        await client.SendAsync(new byte[400], ep);
        var d = Pkt.Discover(0x40);
        d.Xid = 77;
        await client.SendAsync(d.ToBytes(), ep);
        var offer = await ReceiveAsync(client, 77, TimeSpan.FromSeconds(5));
        Assert.Equal(DhcpMessageType.Offer, offer.MessageType);
    }

    [Fact]
    public void Start_Throws_WhenPortIsTaken_WithoutReuse()
    {
        // 端口被一个独占套接字占用时，Start 抛出中文说明的异常
        using var hog = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        hog.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)hog.LocalEndPoint!).Port;
        var engine = EngineFactory.Create(new FakeClock());
        using var server = new DhcpServer(engine, new DhcpServerOptions { ServerIp = IPAddress.Loopback, ServerPort = port, BindAny = false });
        var ex = Assert.Throws<InvalidOperationException>(() => server.Start());
        Assert.Contains("绑定失败", ex.Message);
    }
}

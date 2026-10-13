using System.Net;
using VRAirBridge.Core.Dhcp;
using Xunit;

namespace VRAirBridge.Tests;

public class DhcpEngineTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static (DhcpEngine e, FakeClock c, List<LeaseEvent> ev) New(DhcpLeaseStore? store = null, string start = "192.168.137.100", string end = "192.168.137.199")
    {
        var c = new FakeClock();
        var e = EngineFactory.Create(c, store, start, end);
        var ev = new List<LeaseEvent>();
        e.LeaseChanged += ev.Add;
        return (e, c, ev);
    }

    private static DhcpReply? H(DhcpEngine e, DhcpPacket p) => e.Handle(Pkt.Wire(p), out _);

    [Fact]
    public void Discover_GetsOffer_WithAllRequiredOptions()
    {
        var (e, _, _) = New();
        var r = H(e, Pkt.Discover(1, "iPhone"));
        Assert.NotNull(r);
        var o = Pkt.Wire(r!.Packet);
        Assert.Equal(2, o.Op);
        Assert.Equal(DhcpMessageType.Offer, o.MessageType);
        Assert.Equal(Ip("192.168.137.100"), o.YiAddr);
        Assert.Equal(0x1234u, o.Xid);
        Assert.Equal(Pkt.MacText(1), o.ClientMac);
        Assert.Equal(Ip("255.255.255.0"), o.GetIpOption(DhcpOption.SubnetMask));            // 1
        Assert.Equal(Pkt.Server, o.GetIpOption(DhcpOption.Router));                           // 3
        Assert.Equal(8, o.GetOption(DhcpOption.DnsServers)!.Length);                           // 6
        Assert.Equal(43200u, o.GetUInt32Option(DhcpOption.LeaseTime));                         // 51 = 12h
        Assert.Equal(Pkt.Server, o.GetIpOption(DhcpOption.ServerId));                          // 54
        Assert.Equal(21600u, o.GetUInt32Option(DhcpOption.RenewalTime));                       // 58 = 50%
        Assert.Equal(37800u, o.GetUInt32Option(DhcpOption.RebindingTime));                     // 59 = 87.5%
        Assert.Equal(Ip("192.168.137.255"), o.GetIpOption(DhcpOption.BroadcastAddress));
        Assert.NotNull(o.GetOption(DhcpOption.ClientId));                                      // 61 echoed (RFC 6842)
        Assert.Equal(IPAddress.Broadcast, r.Destination);
        Assert.True(r.Packet.ToBytes().Length >= 300);
    }

    [Fact]
    public void FullHandshake_Selecting_AcksAndStoresLease()
    {
        var (e, _, ev) = New();
        var offer = H(e, Pkt.Discover(1, "iPhone"))!.Packet;
        var ack = H(e, Pkt.Selecting(1, offer.YiAddr, host: "iPhone"));
        Assert.NotNull(ack);
        Assert.Equal(DhcpMessageType.Ack, ack!.Packet.MessageType);
        Assert.Equal(offer.YiAddr, ack.Packet.YiAddr);
        Assert.Equal(IPAddress.Broadcast, ack.Destination);
        var lease = e.Store.GetByMac(Pkt.MacText(1));
        Assert.NotNull(lease);
        Assert.Equal("iPhone", lease!.HostName);
        Assert.NotNull(lease.ClientId);
        Assert.Single(ev);
        Assert.Equal(LeaseEventKind.Granted, ev[0].Kind);
    }

    [Fact]
    public void Selecting_OtherServer_IsSilent_AndReleasesOffer()
    {
        var (e, _, _) = New();
        var offer = H(e, Pkt.Discover(1))!.Packet;
        Assert.Null(H(e, Pkt.Selecting(1, offer.YiAddr, serverId: Ip("192.168.137.254"))));
        // the abandoned offer is no longer held: another client can get the address
        var offer2 = H(e, Pkt.Discover(2))!.Packet;
        Assert.Equal(offer.YiAddr, offer2.YiAddr);
    }

    [Fact]
    public void TwoClients_GetDifferentAddresses_AndOffersAreHeld()
    {
        var (e, _, _) = New();
        var a = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        var b = H(e, Pkt.Discover(2))!.Packet.YiAddr;
        Assert.NotEqual(a, b);
        Assert.Equal(a, H(e, Pkt.Discover(1))!.Packet.YiAddr);
    }

    [Fact]
    public void Discover_HonoursRequestedAddress_WhenFree()
    {
        var (e, _, _) = New();
        var r = H(e, Pkt.Discover(1, requested: Ip("192.168.137.150")))!;
        Assert.Equal(Ip("192.168.137.150"), r.Packet.YiAddr);
        var r2 = H(e, Pkt.Discover(2, requested: Ip("10.0.0.5")))!;
        Assert.True(e.InPool(r2.Packet.YiAddr));
    }

    [Fact]
    public void InitReboot_KnownClient_Acks()
    {
        var (e, c, ev) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        c.Advance(TimeSpan.FromHours(1));
        var r = H(e, Pkt.InitReboot(1, ip));
        Assert.Equal(DhcpMessageType.Ack, r!.Packet.MessageType);
        Assert.Equal(LeaseEventKind.Renewed, ev.Last().Kind);
    }

    [Fact]
    public void InitReboot_WrongSubnet_Naks_WithBroadcastFlag()
    {
        var (e, _, _) = New();
        var r = H(e, Pkt.InitReboot(1, Ip("192.168.1.23")));
        Assert.NotNull(r);
        var nak = Pkt.Wire(r!.Packet);
        Assert.Equal(DhcpMessageType.Nak, nak.MessageType);
        Assert.True(nak.BroadcastFlag);
        Assert.Equal(IPAddress.Any, nak.YiAddr);
        Assert.Equal(IPAddress.Broadcast, r.Destination);
        Assert.Equal(Pkt.Server, nak.GetIpOption(DhcpOption.ServerId));
        Assert.Equal("wrong network", nak.GetStringOption(DhcpOption.Message));
        Assert.Null(nak.GetOption(DhcpOption.LeaseTime));
    }

    [Fact]
    public void InitReboot_AddressHeldByOther_Naks()
    {
        var (e, _, _) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        var r = H(e, Pkt.InitReboot(2, ip));
        Assert.Equal(DhcpMessageType.Nak, r!.Packet.MessageType);
    }

    [Fact]
    public void InitReboot_UnknownClient_FreeAddress_LenientAck()
    {
        // lease file lost: a reconnecting device asking for a free in-pool address is confirmed
        var (e, _, _) = New();
        var r = H(e, Pkt.InitReboot(5, Ip("192.168.137.120")));
        Assert.Equal(DhcpMessageType.Ack, r!.Packet.MessageType);
        Assert.Equal(Ip("192.168.137.120"), r.Packet.YiAddr);
    }

    [Fact]
    public void InitReboot_OnSubnetButOutsidePool_Naks()
    {
        var (e, _, _) = New();
        var r = H(e, Pkt.InitReboot(5, Ip("192.168.137.20")));
        Assert.Equal(DhcpMessageType.Nak, r!.Packet.MessageType);
        Assert.Equal("address not in pool", Pkt.Wire(r.Packet).GetStringOption(DhcpOption.Message));
    }

    [Fact]
    public void Renewing_UnicastsToCiaddr()
    {
        var (e, c, ev) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        c.Advance(TimeSpan.FromHours(6)); // T1
        var r = H(e, Pkt.Renew(1, ip));
        Assert.Equal(DhcpMessageType.Ack, r!.Packet.MessageType);
        Assert.Equal(ip, r.Destination);
        Assert.Equal(ip, r.Packet.CiAddr);
        Assert.Equal(ip, r.Packet.YiAddr);
        Assert.Equal(LeaseEventKind.Renewed, ev.Last().Kind);
        Assert.True(e.Store.GetByMac(Pkt.MacText(1))!.Expires > c.Now.AddHours(11));
    }

    [Fact]
    public void Rebinding_AfterExpiry_StillAcksSameAddressIfFree()
    {
        var (e, c, _) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        c.Advance(TimeSpan.FromHours(13));
        var r = H(e, Pkt.Renew(1, ip));
        Assert.Equal(DhcpMessageType.Ack, r!.Packet.MessageType);
    }

    [Fact]
    public void Renewing_ForeignSubnet_Naks()
    {
        var (e, _, _) = New();
        var r = H(e, Pkt.Renew(1, Ip("10.1.2.3")));
        Assert.Equal(DhcpMessageType.Nak, r!.Packet.MessageType);
    }

    [Fact]
    public void Release_FreesAddress_ButSameClientGetsItBack()
    {
        var (e, c, ev) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        var rel = Pkt.Client(DhcpMessageType.Release, 1);
        rel.CiAddr = ip;
        rel.SetOption(DhcpOption.ServerId, Pkt.Server);
        Assert.Null(H(e, rel));
        Assert.Equal(LeaseEventKind.Released, ev.Last().Kind);
        Assert.False(e.Store.GetByMac(Pkt.MacText(1))!.IsActive(c.Now));
        Assert.Equal(ip, H(e, Pkt.Discover(1))!.Packet.YiAddr);
    }

    [Fact]
    public void Decline_BlocksAddress_AndNextOfferDiffers()
    {
        var (e, _, ev) = New();
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        H(e, Pkt.Selecting(1, ip));
        var dec = Pkt.Client(DhcpMessageType.Decline, 1);
        dec.SetOption(DhcpOption.RequestedIp, ip);
        dec.SetOption(DhcpOption.ServerId, Pkt.Server);
        Assert.Null(H(e, dec));
        Assert.Equal(LeaseEventKind.Declined, ev.Last().Kind);
        Assert.NotEqual(ip, H(e, Pkt.Discover(1))!.Packet.YiAddr);
        Assert.NotEqual(ip, H(e, Pkt.Discover(2))!.Packet.YiAddr);
    }

    [Fact]
    public void Decline_ExpiresAfterTenMinutes()
    {
        var (e, c, _) = New(start: "192.168.137.100", end: "192.168.137.100");
        var ip = H(e, Pkt.Discover(1))!.Packet.YiAddr;
        var dec = Pkt.Client(DhcpMessageType.Decline, 1);
        dec.SetOption(DhcpOption.RequestedIp, ip);
        H(e, dec);
        Assert.Null(H(e, Pkt.Discover(2)));
        c.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(ip, H(e, Pkt.Discover(2))!.Packet.YiAddr);
    }

    [Fact]
    public void Inform_AcksWithoutLeaseOptions_ToCiaddr()
    {
        var (e, _, _) = New();
        var inf = Pkt.Client(DhcpMessageType.Inform, 9);
        inf.CiAddr = Ip("192.168.137.50");
        var r = H(e, inf);
        Assert.NotNull(r);
        var a = Pkt.Wire(r!.Packet);
        Assert.Equal(DhcpMessageType.Ack, a.MessageType);
        Assert.Equal(IPAddress.Any, a.YiAddr);
        Assert.Null(a.GetOption(DhcpOption.LeaseTime));
        Assert.Null(a.GetOption(DhcpOption.RenewalTime));
        Assert.NotNull(a.GetOption(DhcpOption.DnsServers));
        Assert.Equal(Ip("192.168.137.50"), r.Destination);
    }

    [Fact]
    public void PoolExhaustion_NoOffer_ThenExpiredLeaseIsReused()
    {
        var (e, c, _) = New(start: "192.168.137.100", end: "192.168.137.101");
        for (int i = 1; i <= 2; i++)
        {
            var ip = H(e, Pkt.Discover(i))!.Packet.YiAddr;
            H(e, Pkt.Selecting(i, ip));
        }
        Assert.Null(H(e, Pkt.Discover(3)));
        c.Advance(TimeSpan.FromHours(13));
        var r = H(e, Pkt.Discover(3));
        Assert.NotNull(r);
        Assert.True(e.InPool(r!.Packet.YiAddr));
    }

    [Fact]
    public void Ignores_ServerMessages_Bootp_Relayed_NonEthernet()
    {
        var (e, _, _) = New();
        var reply = Pkt.Discover(1); reply.Op = 2;
        Assert.Null(H(e, reply));
        var relayed = Pkt.Discover(1); relayed.GiAddr = Ip("10.0.0.1");
        Assert.Null(H(e, relayed));
        var nonEth = Pkt.Discover(1); nonEth.HType = 6;
        Assert.Null(H(e, nonEth));
        var bootp = Pkt.Discover(1); bootp.Options.Clear();
        Assert.Null(H(e, bootp));
        var offer = Pkt.Client(DhcpMessageType.Offer, 1);
        Assert.Null(H(e, offer));
    }

    [Fact]
    public void StuckClientDetection_ManyOffersNoRequest()
    {
        var (e, c, _) = New();
        for (int i = 0; i < 3; i++) { H(e, Pkt.Discover(4)); c.Advance(TimeSpan.FromSeconds(4)); }
        Assert.Contains(Pkt.MacText(4), e.GetStuckClients());
        var ip = H(e, Pkt.Discover(4))!.Packet.YiAddr;
        H(e, Pkt.Selecting(4, ip));
        Assert.DoesNotContain(Pkt.MacText(4), e.GetStuckClients());
    }

    [Fact]
    public void Leases_PersistAcrossRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrab-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "dhcp-leases.json");
        try
        {
            var (e, _, _) = New(new DhcpLeaseStore(path));
            var ip = H(e, Pkt.Discover(1, "Quest-3"))!.Packet.YiAddr;
            H(e, Pkt.Selecting(1, ip, host: "Quest-3"));
            Assert.True(File.Exists(path));

            var store2 = new DhcpLeaseStore(path);
            var l = store2.GetByMac(Pkt.MacText(1));
            Assert.NotNull(l);
            Assert.Equal(ip.ToString(), l!.Ip);
            Assert.Equal("Quest-3", l.HostName);
            var (e2, _, _) = New(store2);
            Assert.Equal(ip, H(e2, Pkt.Discover(1))!.Packet.YiAddr);
            Assert.NotEqual(ip, H(e2, Pkt.Discover(2))!.Packet.YiAddr);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

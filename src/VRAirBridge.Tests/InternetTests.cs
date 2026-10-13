using System.Net;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>“允许连接的设备访问互联网”：DHCP 下发的选项 + NAT / 转发的处理。</summary>
public class InternetTests
{
    // ---------------------------------------------------------------- DHCP

    private static DhcpEngine Engine(bool allowInternet, FakeClock clock)
    {
        var s = new DhcpSettings { PoolStart = "192.168.137.100", PoolEnd = "192.168.137.199", LeaseHours = 12, DomainName = "lan" };
        var cfg = DhcpEngineConfig.Create(Pkt.Server, Pkt.Mask, s, new[] { IPAddress.Parse("223.5.5.5"), IPAddress.Parse("8.8.8.8") }, allowInternet);
        return new DhcpEngine(cfg, new DhcpLeaseStore(null), clock.Get) { Synchronous = true };
    }

    private static DhcpPacket Reply(DhcpEngine e, DhcpPacket p) => Pkt.Wire(e.Handle(Pkt.Wire(p), out _)!.Packet);

    private static void AssertBaseOptions(DhcpPacket o)
    {
        Assert.Equal(Pkt.Mask, o.GetIpOption(DhcpOption.SubnetMask));          // 1
        Assert.Equal(Pkt.Server, o.GetIpOption(DhcpOption.Router));           // 3 网关 = 本机
        Assert.Equal(43200u, o.GetUInt32Option(DhcpOption.LeaseTime));         // 51
        Assert.Equal(Pkt.Server, o.GetIpOption(DhcpOption.ServerId));          // 54
    }

    [Fact]
    public void Allowed_OfferAndAck_HaveDnsAndDomain()
    {
        var e = Engine(true, new FakeClock());
        var offer = Reply(e, Pkt.Discover(1, "Quest"));
        AssertBaseOptions(offer);
        Assert.Equal(8, offer.GetOption(DhcpOption.DnsServers)!.Length);       // 6
        Assert.NotNull(offer.GetOption(DhcpOption.DomainName));                 // 15
        var ack = Reply(e, Pkt.Selecting(1, offer.YiAddr));
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        AssertBaseOptions(ack);
        Assert.NotNull(ack.GetOption(DhcpOption.DnsServers));
    }

    [Fact]
    public void LanOnly_OfferAndAck_OmitDnsAndDomain_ButKeepAddressMaskLeaseServerRouter()
    {
        var e = Engine(false, new FakeClock());
        Assert.Empty(e.Config.AdvertisedDns);
        var offer = Reply(e, Pkt.Discover(1, "Quest"));
        Assert.Equal(DhcpMessageType.Offer, offer.MessageType);
        Assert.Equal(IPAddress.Parse("192.168.137.100"), offer.YiAddr);
        AssertBaseOptions(offer);
        Assert.Null(offer.GetOption(DhcpOption.DnsServers));
        Assert.Null(offer.GetOption(DhcpOption.DomainName));

        var ack = Reply(e, Pkt.Selecting(1, offer.YiAddr));
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        Assert.Equal(offer.YiAddr, ack.YiAddr);
        AssertBaseOptions(ack);
        Assert.Null(ack.GetOption(DhcpOption.DnsServers));
        Assert.Null(ack.GetOption(DhcpOption.DomainName));
    }

    [Fact]
    public void LiveToggle_UpdateConfig_AffectsNextReply()
    {
        var e = Engine(true, new FakeClock());
        var offer = Reply(e, Pkt.Discover(1));
        Assert.NotNull(offer.GetOption(DhcpOption.DnsServers));
        e.UpdateConfig(e.Config with { AllowInternet = false });
        var renew = Reply(e, Pkt.Selecting(1, offer.YiAddr));
        Assert.Equal(DhcpMessageType.Ack, renew.MessageType);
        Assert.Null(renew.GetOption(DhcpOption.DnsServers));
        e.UpdateConfig(e.Config with { AllowInternet = true });
        Assert.NotNull(Reply(e, Pkt.Renew(1, offer.YiAddr)).GetOption(DhcpOption.DnsServers));
    }

    // ---------------------------------------------------------------- NAT / 转发

    private sealed class FakeNetOps : INetOps
    {
        public readonly List<NatInfo> Nats = new();
        public readonly Dictionary<int, bool?> Forwarding = new();
        public readonly List<string> Calls = new();
        public bool FailRemove;

        public List<NatInfo> GetNats() => Nats.ToList();
        public bool CreateNat(string name, string prefix, out string error)
        {
            Calls.Add($"create {name} {prefix}");
            Nats.Add(new NatInfo(name, prefix, true));
            error = "";
            return true;
        }
        public bool RemoveNat(string name)
        {
            Calls.Add("remove " + name);
            if (FailRemove) return false;
            Nats.RemoveAll(n => n.Name == name);
            return true;
        }
        public bool? GetForwarding(int ifIndex) => Forwarding.TryGetValue(ifIndex, out var v) ? v : null;
        public bool SetForwarding(int ifIndex, bool enabled, out string error)
        {
            Calls.Add($"fwd {ifIndex} {(enabled ? "on" : "off")}");
            Forwarding[ifIndex] = enabled;
            error = "";
            return true;
        }
    }

    private const int GoIf = 21, UpIf = 7;
    private static readonly GoAdapterInfo Go = new("本地连接* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2", "{GO}", "02-E0-4C-00-00-01", GoIf,
        IPAddress.Parse("192.168.137.1"), IPAddress.Parse("255.255.255.0"));
    private static readonly UplinkInfo Up = new("以太网", "Intel(R) Ethernet", "{UP}", UpIf, IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.1"),
        new[] { IPAddress.Parse("10.0.0.1") });

    private static (NatManager nat, AppSettings s, FakeNetOps ops, Func<int> saves) New(bool allow)
    {
        var s = new AppSettings();
        s.Internet.Allow = allow;
        var ops = new FakeNetOps();
        int saves = 0;
        return (new NatManager(() => s, () => saves++, ops), s, ops, () => saves);
    }

    [Fact]
    public void Allowed_CreatesNat_AndEnablesForwarding_RecordedForRevert()
    {
        var (nat, s, ops, saves) = New(allow: true);
        ops.Forwarding[GoIf] = false;
        ops.Forwarding[UpIf] = false;
        var st = nat.Reconcile(Go, Up, icsServing: false, isAdmin: true);
        Assert.True(st.Ok);
        Assert.Equal(NatProvider.Ours, st.Provider);
        Assert.Contains("create VRAirBridgeNat 192.168.137.0/24", ops.Calls);
        Assert.True(ops.Forwarding[GoIf]);
        Assert.True(ops.Forwarding[UpIf]);
        Assert.True(s.Revert.NatCreatedByUs);
        Assert.Equal(new[] { Go.Name, Up.Name }, s.Revert.ForwardingEnabledOn);
        Assert.True(saves() > 0);
    }

    [Fact]
    public void LanOnly_RemovesOurNat_DisablesGoForwarding_AndUndoesOurUplinkForwarding()
    {
        var (nat, s, ops, _) = New(allow: false);
        ops.Nats.Add(new NatInfo("VRAirBridgeNat", "192.168.137.0/24", true));
        ops.Forwarding[GoIf] = true;
        ops.Forwarding[UpIf] = true;
        s.Revert.NatCreatedByUs = true;
        s.Revert.ForwardingEnabledOn.AddRange(new[] { Go.Name, Up.Name });

        var st = nat.Reconcile(Go, Up, icsServing: false, isAdmin: true);
        Assert.True(st.Ok);
        Assert.Equal(NatProvider.LanOnly, st.Provider);
        Assert.Equal(InternetStrings.NatLanOnly, st.Text);
        Assert.Empty(ops.Nats);
        Assert.False(ops.Forwarding[GoIf]);
        Assert.False(ops.Forwarding[UpIf]);
        Assert.False(s.Revert.NatCreatedByUs);
        Assert.Empty(s.Revert.ForwardingEnabledOn);
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public void LanOnly_LeavesForeignNatAndUplinkForwardingAlone()
    {
        var (nat, s, ops, _) = New(allow: false);
        ops.Nats.Add(new NatInfo("DockerNAT", "172.17.0.0/16", true));
        ops.Forwarding[GoIf] = false;
        ops.Forwarding[UpIf] = true;   // 不是本软件打开的
        var st = nat.Reconcile(Go, Up, icsServing: false, isAdmin: true);
        Assert.True(st.Ok);
        Assert.Single(ops.Nats);
        Assert.True(ops.Forwarding[UpIf]);
        Assert.Empty(ops.Calls);
    }

    [Fact]
    public void LanOnly_NotAdmin_ReportsNeedAdmin_AndKeepsRevertRecords()
    {
        var (nat, s, ops, _) = New(allow: false);
        ops.Nats.Add(new NatInfo("VRAirBridgeNat", "192.168.137.0/24", true));
        ops.Forwarding[GoIf] = true;
        s.Revert.NatCreatedByUs = true;
        s.Revert.ForwardingEnabledOn.Add(Go.Name);
        var st = nat.Reconcile(Go, Up, icsServing: false, isAdmin: false);
        Assert.False(st.Ok);
        Assert.Equal(NatProvider.LanOnly, st.Provider);
        Assert.Equal(InternetStrings.NatLanOnlyNeedAdmin, st.Text);
        Assert.Single(ops.Nats);
        Assert.Empty(ops.Calls);
        Assert.True(s.Revert.NatCreatedByUs);
        Assert.Contains(Go.Name, s.Revert.ForwardingEnabledOn);
    }

    [Fact]
    public void LanOnly_HotspotOff_RemovesNatOnly()
    {
        var (nat, _, ops, _) = New(allow: false);
        ops.Nats.Add(new NatInfo("VRAirBridgeNat", "192.168.137.0/24", true));
        var st = nat.Reconcile(null, null, icsServing: false, isAdmin: true);
        Assert.True(st.Ok);
        Assert.Equal(new[] { "remove VRAirBridgeNat" }, ops.Calls);
    }

    [Fact]
    public void LanOnly_WithIcs_WarnsThatIcsStillRoutes()
    {
        var (nat, _, ops, _) = New(allow: false);
        ops.Forwarding[GoIf] = false;
        var st = nat.Reconcile(Go, Up, icsServing: true, isAdmin: true);
        Assert.False(st.Ok);
        Assert.Equal(InternetStrings.NatLanOnlyIcs, st.Text);
    }

    [Fact]
    public void LanOnly_RemoveFails_ReportsNotOk()
    {
        var (nat, _, ops, _) = New(allow: false);
        ops.Nats.Add(new NatInfo("VRAirBridgeNat", "192.168.137.0/24", true));
        ops.FailRemove = true;
        ops.Forwarding[GoIf] = false;
        var st = nat.Reconcile(Go, Up, icsServing: false, isAdmin: true);
        Assert.False(st.Ok);
        Assert.Contains("删除 NAT", st.Text);
    }

    [Fact]
    public void Invalidate_BypassesForwardingThrottle()
    {
        var (nat, _, ops, _) = New(allow: false);
        ops.Forwarding[GoIf] = true;
        nat.Reconcile(Go, Up, false, true);
        Assert.False(ops.Forwarding[GoIf]);
        // 别的程序又打开了转发：30 秒节流内不会重查
        ops.Forwarding[GoIf] = true;
        nat.Reconcile(Go, Up, false, true);
        Assert.True(ops.Forwarding[GoIf]);
        nat.Invalidate();
        nat.Reconcile(Go, Up, false, true);
        Assert.False(ops.Forwarding[GoIf]);
    }

    [Fact]
    public void ToggleBackOn_RecreatesNatImmediately()
    {
        var (nat, s, ops, _) = New(allow: false);
        ops.Forwarding[GoIf] = false;
        ops.Forwarding[UpIf] = false;
        Assert.Equal(NatProvider.LanOnly, nat.Reconcile(Go, Up, false, true).Provider);
        s.Internet.Allow = true;
        nat.Invalidate();
        var st = nat.Reconcile(Go, Up, false, true);
        Assert.Equal(NatProvider.Ours, st.Provider);
        Assert.True(ops.Forwarding[GoIf]);
        Assert.Single(ops.Nats);
    }

    [Fact]
    public void StatusText_IsChinese()
    {
        Assert.Equal("互联网：已允许", InternetStrings.StatusText(true));
        Assert.Equal("互联网：仅局域网", InternetStrings.StatusText(false));
        Assert.True(new AppSettings().Internet.Allow);
    }
}

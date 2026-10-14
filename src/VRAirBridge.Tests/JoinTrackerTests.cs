using VRAirBridge.Core;
using VRAirBridge.Core.Clients;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>
/// The join monitor on recorded event sequences: (a) password check failed, (b) dropped right after the password
/// check, (c) no IP, (d) reconnect loop, (e) success, and leaves caused by a hotspot stop.
/// </summary>
public class JoinTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 10, 27, 0, DateTimeKind.Utc);
    private const string Guid1 = "{6C3F0C1E-1111-2222-3333-444455556666}";
    /// <summary>A randomized "private Wi-Fi address" (locally administered bit set), like an iPhone uses.</summary>
    private const string Phone = "1A6EB50C4C34";
    private const string PhoneText = "1A-6E-B5-0C-4C-34";

    private static WlanEvent Ev(int id, double sec, string peer = "", string? cid = null) => new()
    {
        Id = id,
        TimeUtc = T0.AddSeconds(sec),
        InterfaceGuid = Guid1,
        PeerMac = peer,
        ConnectionId = cid,
    };

    private static JoinTracker NewTracker() => new() { EventsAvailable = true };

    private static JoinContext Ours(bool wpa3 = false) => new() { Dhcp = JoinDhcp.Ours, Wpa3 = wpa3 };

    /// <summary>20019 associate, 11010 security start (ConnectionId learned).</summary>
    private static void Associate(JoinTracker t, double sec, string cid)
    {
        t.OnEvent(Ev(20019, sec, Phone));
        t.OnEvent(Ev(11010, sec + 0.005, cid: cid));
    }

    /// <summary>20020 peer authenticated (PeerMAC is a counter there) and 11005 security succeeded.</summary>
    private static void Authenticate(JoinTracker t, double sec, string? cid)
    {
        t.OnEvent(Ev(20020, sec, "010001000000", cid));
        t.OnEvent(Ev(11005, sec + 0.001, cid: cid));
    }

    [Fact]
    public void HandshakeNeverFinished_IsWrongPassword()
    {
        var t = NewTracker();
        Associate(t, 0, "c1");
        t.OnEvent(Ev(11004, 1.0, cid: "c1"));
        Assert.Empty(t.Tick(T0.AddSeconds(1.5), Ours())); // waits for Settle
        var f = Assert.Single(t.Tick(T0.AddSeconds(4), Ours()));
        Assert.Equal(JoinFailure.WrongPassword, f.Failure);
        Assert.False(f.IsSuccess);
        Assert.Equal(PhoneText, f.Record.Mac);
        Assert.True(f.Record.IsPrivateAddress);
        Assert.Contains(PhoneText, f.Message);

        var rec = t.Get(PhoneText)!;
        Assert.Equal(JoinStage.Left, rec.Stage);
        Assert.Equal(JoinFailure.WrongPassword, rec.Failure);
        Assert.False(rec.IsConnected);
    }

    [Fact]
    public void NoAuthWithinTimeout_IsWrongPassword_AndRateLimited()
    {
        var t = NewTracker();
        t.OnEvent(Ev(20019, 0, Phone));
        Assert.Empty(t.Tick(T0.AddSeconds(5), Ours()));
        var f = Assert.Single(t.Tick(T0.AddSeconds(9), Ours()));
        Assert.Equal(JoinFailure.WrongPassword, f.Failure);

        // the same failure again a minute later: recorded, but not reported twice within 5 minutes
        t.OnEvent(Ev(20019, 60, Phone));
        Assert.Empty(t.Tick(T0.AddSeconds(70), Ours()));
        var rec = t.Get(Phone)!;
        Assert.Equal(2, rec.Attempts);
        Assert.Equal(JoinFailure.WrongPassword, rec.Failure);
        Assert.Single(t.History());
    }

    [Fact]
    public void LeftRightAfterAuth_IsDroppedAfterAuth()
    {
        // the iPhone case: authenticated, then gone 80 ms later, no DHCP at all
        var t = NewTracker();
        Associate(t, 0, "c7");
        Authenticate(t, 0.010, "c7");
        Assert.Equal(JoinStage.Authenticated, t.Get(Phone)!.Stage);
        t.OnEvent(Ev(11004, 0.090, cid: "c7"));
        var f = Assert.Single(t.Tick(T0.AddSeconds(3), Ours()));
        Assert.Equal(JoinFailure.DroppedAfterAuth, f.Failure);
        Assert.Equal(0, f.ElapsedSeconds);
        Assert.False(f.DhcpRequestsSeen);
        Assert.Equal(Loc.T("join.hintDropped"), JoinText.Hint(JoinFailure.DroppedAfterAuth, JoinDhcp.Ours, false, wpa3: false));
    }

    [Fact]
    public void DroppedAfterAuth_WithWpa3_SuggestsWpa2()
    {
        var t = NewTracker();
        Associate(t, 0, "c8");
        Authenticate(t, 0.010, "c8");
        t.OnEvent(Ev(11004, 0.5, cid: "c8"));
        var f = Assert.Single(t.Tick(T0.AddSeconds(3), Ours(wpa3: true)));
        Assert.Equal(JoinFailure.DroppedAfterAuth, f.Failure);
        Assert.True(f.Wpa3);
        Assert.Contains(Loc.T("join.hintWpa3"), f.Message);
    }

    [Fact]
    public void DhcpPacketsSeen_IsNotADrop()
    {
        var t = NewTracker();
        Associate(t, 0, "c9");
        Authenticate(t, 0.010, "c9");
        t.OnEvent(Ev(11004, 2.0, cid: "c9"));
        var ctx = Ours().AddDhcpPacket(Phone, T0.AddSeconds(1));
        var f = Assert.Single(t.Tick(T0.AddSeconds(5), ctx));
        Assert.Equal(JoinFailure.NoIp, f.Failure);
        Assert.True(f.DhcpRequestsSeen);
    }

    [Fact]
    public void AuthenticatedWithoutIp_IsNoIp()
    {
        var t = NewTracker();
        Associate(t, 0, "c2");
        Authenticate(t, 0.010, "c2");
        Assert.Empty(t.Tick(T0.AddSeconds(10), Ours()));
        var f = Assert.Single(t.Tick(T0.AddSeconds(16), Ours()));
        Assert.Equal(JoinFailure.NoIp, f.Failure);
        Assert.Equal(JoinDhcp.Ours, f.Dhcp);
        Assert.True(f.OurDhcp);
        Assert.Equal(JoinStage.Authenticated, t.Get(Phone)!.Stage);
        // reported once per attempt
        Assert.Empty(t.Tick(T0.AddSeconds(30), Ours()));
    }

    [Fact]
    public void LeaseArrives_IsSuccess()
    {
        var t = NewTracker();
        t.OnEvent(Ev(20019, 0, Phone));
        Authenticate(t, 0.010, cid: null); // no ConnectionId: matched to the latest association
        var ctx = Ours().AddLease(PhoneText, "192.168.137.101", "Alans-iPhone", T0.AddSeconds(1.5), active: true);
        var f = Assert.Single(t.Tick(T0.AddSeconds(2), ctx));
        Assert.True(f.IsSuccess);
        Assert.Null(f.Failure);
        var rec = t.Get(Phone)!;
        Assert.Equal(JoinStage.GotIp, rec.Stage);
        Assert.Equal("192.168.137.101", rec.Ip);
        Assert.Equal("Alans-iPhone", rec.HostName);
        Assert.Null(rec.Failure);
        Assert.Contains("192.168.137.101", f.Message);
    }

    [Fact]
    public void SuccessClearsAnEarlierFailure()
    {
        var t = NewTracker();
        t.OnEvent(Ev(20019, 0, Phone));
        Assert.Single(t.Tick(T0.AddSeconds(9), Ours()));
        Assert.Equal(JoinFailure.WrongPassword, t.Get(Phone)!.Failure);

        t.OnEvent(Ev(20019, 30, Phone));
        Authenticate(t, 30.01, null);
        var ctx = Ours().AddLease(Phone, "192.168.137.102", null, T0.AddSeconds(31), active: true);
        Assert.True(Assert.Single(t.Tick(T0.AddSeconds(32), ctx)).IsSuccess);
        Assert.Null(t.Get(Phone)!.Failure);
    }

    [Fact]
    public void RepeatedShortConnections_AreAReconnectLoop()
    {
        var t = NewTracker();
        var all = new List<JoinFinding>();
        foreach (var s in new[] { 0.0, 40.0, 80.0 })
        {
            t.OnEvent(Ev(20019, s, Phone));
            Authenticate(t, s + 0.01, null);
            t.OnEvent(Ev(11004, s + 10)); // no ConnectionId: the only connected device
            all.AddRange(t.Tick(T0.AddSeconds(s + 13), Ours()));
        }
        Assert.Equal(new JoinFailure?[] { JoinFailure.NoIp, JoinFailure.ReconnectLoop }, all.Select(f => f.Failure).ToArray());
        var loop = all[1];
        Assert.Equal(3, loop.Cycles);
        Assert.Equal(40, loop.IntervalSeconds);
        Assert.Equal(JoinFailure.ReconnectLoop, t.Get(Phone)!.Failure);
    }

    [Fact]
    public void HotspotStop_IsNotTheDevicesFault()
    {
        var t = NewTracker();
        Associate(t, 0, "c3");
        t.OnEvent(Ev(8008, 1.0)); // hotspot stopping 1 s after the association: normal handshake still possible
        Assert.Empty(t.Tick(T0.AddSeconds(5), Ours()));
        Assert.Equal(JoinStage.Left, t.Get(Phone)!.Stage);
        Assert.Null(t.Get(Phone)!.Failure);
    }

    [Fact]
    public void StalledHandshake_BeforeAStop_IsStillWrongPassword()
    {
        // the 18:27 case: the password step hung for seconds, then a settings change restarted the hotspot
        var t = NewTracker();
        Associate(t, 0, "c4");
        t.OnEvent(Ev(8008, 4.0));
        var f = Assert.Single(t.Tick(T0.AddSeconds(7), Ours()));
        Assert.Equal(JoinFailure.WrongPassword, f.Failure);
    }

    [Fact]
    public void ConnectedStation_StoppedByHotspotStop_NoFinding()
    {
        var t = NewTracker();
        t.OnEvent(Ev(20019, 0, Phone));
        Authenticate(t, 0.01, null);
        Assert.Single(t.Tick(T0.AddSeconds(2), Ours().AddLease(Phone, "192.168.137.101", null, T0.AddSeconds(1), true)));
        t.HotspotStopped(T0.AddSeconds(100));
        Assert.Empty(t.Tick(T0.AddSeconds(105), Ours()));
        Assert.Equal(JoinStage.Left, t.Get(Phone)!.Stage);
        Assert.Null(t.Get(Phone)!.Ip);
    }

    [Fact]
    public void ClientListOnly_WithoutEvents()
    {
        var t = new JoinTracker { EventsAvailable = false };
        var now = T0.AddMinutes(5);
        var ctx = Ours().AddPresent("2C-26-17-4A-9B-01", "Quest-3").AddLease("2C-26-17-4A-9B-01", "192.168.137.101", null, now.AddMinutes(-1), true);
        t.Tick(now, ctx);
        var rec = t.Get("2c:26:17:4a:9b:01")!;
        Assert.Equal(JoinStage.GotIp, rec.Stage);
        Assert.Equal("Quest-3", rec.HostName);
        Assert.False(rec.IsPrivateAddress);
    }

    [Fact]
    public void OtherInterfaceEvents_AreIgnored()
    {
        var t = NewTracker();
        t.OnEvent(Ev(20019, 0, Phone));
        // a 20020 on another interface must not authenticate the phone
        t.OnEvent(new WlanEvent { Id = 20020, TimeUtc = T0.AddSeconds(0.01), InterfaceGuid = "{00000000-1111-2222-3333-999999999999}" });
        Assert.Equal(JoinStage.Associated, t.Get(Phone)!.Stage);
    }

    [Fact]
    public void Hints()
    {
        Assert.Equal(Loc.T("join.hintWrongPassword"), JoinText.Hint(JoinFailure.WrongPassword, JoinDhcp.Ours, false, false));
        Assert.Equal(Loc.T("join.hintWpa3"), JoinText.Hint(JoinFailure.DroppedAfterAuth, JoinDhcp.Ours, false, true));
        Assert.Equal(Loc.T("join.hintFirewall"), JoinText.Hint(JoinFailure.NoIp, JoinDhcp.Ours, true, false));
        Assert.Equal(Loc.T("join.hintRejoin"), JoinText.Hint(JoinFailure.NoIp, JoinDhcp.Ours, false, false));
        Assert.Equal(Loc.T("join.hintIcs"), JoinText.Hint(JoinFailure.ReconnectLoop, JoinDhcp.Ics, false, false));
        Assert.Equal(Loc.T("join.hintNone"), JoinText.Hint(JoinFailure.NoIp, JoinDhcp.Other, false, false));
    }
}

public class WlanEventTests
{
    private const string Xml =
        "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">" +
        "<System><Provider Name=\"Microsoft-Windows-WLAN-AutoConfig\" /><EventID>{0}</EventID>" +
        "<TimeCreated SystemTime=\"2026-10-13T10:27:01.1234567Z\" /><EventRecordID>4242</EventRecordID></System>" +
        "<EventData>" +
        "<Data Name=\"InterfaceGuid\">{{6C3F0C1E-1111-2222-3333-444455556666}}</Data>" +
        "<Data Name=\"InterfaceDescription\">Microsoft Wi-Fi Direct Virtual Adapter #3</Data>" +
        "<Data Name=\"LocalMAC\">02:11:22:33:44:55</Data>" +
        "<Data Name=\"PeerMAC\">1a:6e:b5:0c:4c:34</Data>" +
        "<Data Name=\"ReasonCode\">5</Data>" +
        "</EventData></Event>";

    [Fact]
    public void FromXml_Association()
    {
        var e = WlanEvent.FromXml(string.Format(Xml, 20019))!;
        Assert.NotNull(e);
        Assert.Equal(20019, e.Id);
        Assert.Equal(WlanEventKind.Associated, e.Kind);
        Assert.Equal(new DateTime(2026, 10, 13, 10, 27, 1, DateTimeKind.Utc).AddTicks(1234567), e.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, e.TimeUtc.Kind);
        Assert.Equal(4242, e.RecordId);
        Assert.Equal("{6C3F0C1E-1111-2222-3333-444455556666}", e.InterfaceGuid);
        Assert.Equal("021122334455", e.LocalMac);
        Assert.Equal("1A6EB50C4C34", e.PeerMac);
        Assert.Equal(5, e.ReasonCode);
        Assert.True(e.IsWifiDirect);
    }

    [Fact]
    public void FromXml_Rejects()
    {
        Assert.Null(WlanEvent.FromXml(string.Format(Xml, 12345))); // not an event of interest
        Assert.Null(WlanEvent.FromXml("<not xml"));
        Assert.Null(WlanEvent.FromXml("<Event><EventData /></Event>"));
    }

    [Fact]
    public void Kinds()
    {
        foreach (var id in WlanEvent.EventIds) Assert.NotEqual(WlanEventKind.Other, WlanEvent.KindOf(id));
        Assert.Equal(WlanEventKind.Other, WlanEvent.KindOf(1));
    }

    [Fact]
    public void InterfaceFilter_LearnsGuidsFromTheMac()
    {
        var f = new HotspotInterfaceFilter();
        Assert.False(f.HasInterface);
        f.Add("{6C3F0C1E-1111-2222-3333-444455556666}", "02-11-22-33-44-55");
        Assert.True(f.HasInterface);
        Assert.True(f.Matches(new WlanEvent { InterfaceGuid = "6c3f0c1e-1111-2222-3333-444455556666" }));
        // the hotspot restarted on a new virtual adapter: same MAC, new GUID
        const string g2 = "{AAAAAAAA-1111-2222-3333-444455556666}";
        Assert.True(f.Matches(new WlanEvent { InterfaceGuid = g2, LocalMac = "021122334455" }));
        Assert.True(f.Matches(new WlanEvent { InterfaceGuid = g2 })); // learned
        // the internal Wi-Fi card
        Assert.False(f.Matches(new WlanEvent { InterfaceGuid = "{BBBBBBBB-1111-2222-3333-444455556666}", LocalMac = "BC2228C80000" }));
        Assert.False(f.Matches(new WlanEvent { Description = "Microsoft Wi-Fi Direct Virtual Adapter #2" }));
        var any = new HotspotInterfaceFilter { AnyWifiDirect = true };
        Assert.True(any.Matches(new WlanEvent { Description = "Microsoft Wi-Fi Direct Virtual Adapter #2" }));
        Assert.False(any.Matches(new WlanEvent { Description = "Realtek 8852CE Wireless LAN WiFi 6E PCI-E NIC" }));
    }
}

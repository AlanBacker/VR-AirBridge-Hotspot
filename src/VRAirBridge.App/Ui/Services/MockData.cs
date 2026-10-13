using System.Net;
using VRAirBridge.App.Ui.ViewModels;
using VRAirBridge.Core;
using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;

namespace VRAirBridge.App.Ui.Services;

/// <summary>One screenshot state: mock settings + status + log, plus an optional tweak of the view model.</summary>
internal sealed record MockScene(string Name, AppSettings Settings, AppStatus Status, bool IsAdmin, IReadOnlyList<LogEntry> Log,
    Action<MainViewModel>? Setup = null);

/// <summary>
/// Fake data for <c>--ui-screenshot</c>. Never touches config.json: the settings are built here, with a demo SSID and a
/// demo password, so no real credentials can end up in a PNG. Strings come from the current language.
/// </summary>
internal static class MockData
{
    internal const string DemoSsid = "VR-AirBridge-Demo";
    private const string DemoPassword = "demo-pass-2468";
    private const string UplinkSsid = "HomeWiFi-5G";

    internal static readonly string[] SceneNames = { "start", "stopped", "running", "lanonly", "pin", "error" };

    internal static MockScene Build(string name) => name switch
    {
        "start" => Start(),
        "stopped" => Stopped(),
        "running" => Running(lanOnly: false),
        "lanonly" => Running(lanOnly: true),
        "pin" => Pinned(),
        "error" => Error(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>
    /// Layout check: the running dashboard with a log long enough to overflow its list (85 lines, a few of them long
    /// enough to wrap in the compact layout). The newest line is <see cref="NewestLongLogLine"/>.
    /// </summary>
    internal static MockScene LongLog() => Running(lanOnly: false) with { Name = "longlog", Log = LongLogEntries() };

    internal static string NewestLongLogLine => Loc.T("internet.appliedOn");

    // ------------------------------------------------------------------ scenes

    private static MockScene Start()
    {
        var s = Settings(setupDone: false);
        var drv = new DriverState(DriverStateKind.PresentNoDriver)
        {
            DeviceName = "802.11ax WLAN Adapter",
            PnpId = @"USB\VID_2001&PID_3323\00E04C000001",
            ErrorCode = 28,
        };
        var st = new AppStatus
        {
            State = OverallState.Stopped,
            Headline = AppStatus.HeadlineFor(OverallState.Stopped),
            Detail = Loc.T("watchdog.clickStartHotspotTurnIt"),
            IsAdmin = true,
            Hotspot = Snapshot(HotspotState.Off, 0),
            Driver = drv,
            Hints = new[] { DriverStrings.InlineWarning },
            InternetAllowed = true,
        };
        return new MockScene("start", s, st, true, Log(running: false));
    }

    private static MockScene Stopped()
    {
        var s = Settings(setupDone: true);
        var st = new AppStatus
        {
            State = OverallState.Stopped,
            Headline = AppStatus.HeadlineFor(OverallState.Stopped),
            Detail = Loc.T("watchdog.clickStartHotspotTurnIt"),
            IsAdmin = false,
            Hints = new[] { Loc.T("watchdog.notRunningAsAdministratorBuilt") }, // what the watchdog reports when not admin
            Hotspot = Snapshot(HotspotState.Off, 0),
            Dongle = Dongle(),
            Driver = DriverOk(),
            Dhcp = new DhcpStatus(DhcpProvider.None, Loc.T("dhcp.notRunning"), null),
            Nat = new NatStatus(NatProvider.None, Loc.T("status.notRunning"), false),
            Channel = Channel(goChannel: null, uplinkChannel: 36),
            InternetAllowed = true,
        };
        return new MockScene("stopped", s, st, false, Log(running: false, admin: false));
    }

    private static MockScene Running(bool lanOnly)
    {
        var s = Settings(setupDone: true);
        s.Internet.Allow = !lanOnly;
        var clients = Clients();
        var st = RunningStatus(s, clients, goChannel: 149, pin: null, lanOnly);
        return new MockScene(lanOnly ? "lanonly" : "running", s, st, true, Log(running: true),
            vm => vm.SecNetwork.IsExpanded = true);
    }

    private static MockScene Pinned()
    {
        var s = Settings(setupDone: true);
        s.Ui.Advanced = true;
        s.Channel.PinSsid = UplinkSsid;
        var pin = new PinStatus(PinPhase.Verified, UplinkSsid, 36, string.Format(ChannelStrings.PinVerified, UplinkSsid, 36));
        var st = RunningStatus(s, Clients().Take(1).ToList(), goChannel: 36, pin, lanOnly: false);
        return new MockScene("pin", s, st, true, Log(running: true), vm =>
        {
            vm.SecHotspot.IsExpanded = false;
            vm.SecNetwork.IsExpanded = false;
        });
    }

    private static MockScene Error()
    {
        var s = Settings(setupDone: true);
        var snap = Snapshot(HotspotState.Error, 0) with { Error = Loc.T("watchdog.hotspotUnavailable") };
        var st = new AppStatus
        {
            State = OverallState.Error,
            Headline = AppStatus.HeadlineFor(OverallState.Error),
            Detail = Loc.T("watchdog.hotspotUnavailable"),
            DesiredOn = true,
            IsAdmin = true,
            Hotspot = snap,
            Dongle = Dongle(),
            Driver = DriverOk(),
            Dhcp = new DhcpStatus(DhcpProvider.None, Loc.T("dhcp.notRunning"), null),
            Nat = new NatStatus(NatProvider.None, Loc.T("status.notRunning"), false),
            Channel = Channel(goChannel: null, uplinkChannel: 36),
            InternetAllowed = true,
        };
        return new MockScene("error", s, st, true, Log(running: false, error: true), vm =>
            vm.Toasts.Show(Loc.T("ui.opFailed", Loc.T("watchdog.hotspotUnavailable")), Tone.Bad, persistent: true));
    }

    // ------------------------------------------------------------------ pieces

    private static AppSettings Settings(bool setupDone)
    {
        var s = new AppSettings
        {
            Ssid = DemoSsid,
            Password = DemoPassword,
            Band = "5GHz",
            Auth = "WPA2",
            AutoStart = true,
            StartHotspotOnLaunch = true,
            MinimizeToTray = true,
            KeepHotspotOnClose = true,
            ShowDebugLog = false,
        };
        s.Ui.SetupDone = setupDone;
        s.Ui.Language = Loc.Current;
        return s;
    }

    private static AppStatus RunningStatus(AppSettings s, IReadOnlyList<ClientRow> clients, int goChannel, PinStatus? pin, bool lanOnly)
    {
        int online = clients.Count(c => c.Online);
        var snap = Snapshot(HotspotState.On, online);
        var dhcpDetail = Loc.T("dhcp.listeningReceivedPacketsSentReplies", "192.168.137.1:67", 18, 18);
        return new AppStatus
        {
            State = OverallState.Running,
            Headline = AppStatus.HeadlineFor(OverallState.Running),
            Detail = Loc.T("watchdog.nameDevicesConnected", DemoSsid, snap.BandText, online, InternetStrings.StatusText(!lanOnly)),
            DesiredOn = true,
            IsAdmin = true,
            Hotspot = snap,
            Dongle = Dongle(),
            Go = new GoAdapterInfo("Local Area Connection* 12", "Microsoft Wi-Fi Direct Virtual Adapter #2",
                "{6A3F0C1E-0000-4000-8000-000000000012}", "02-E0-4C-68-00-01", 23,
                IPAddress.Parse("192.168.137.1"), IPAddress.Parse("255.255.255.0")),
            GoOnDongle = true,
            Beacon = new BeaconResult(DemoSsid, "02:e0:4c:68:00:01", new BeaconInfo(goChannel, 5000 + goChannel * 5, 80, "802.11ax", 2),
                -37, Array.Empty<string>(), "Intel(R) Wi-Fi 6E AX211 160MHz", DateTime.Now.AddMinutes(-3)),
            Dhcp = lanOnly
                ? new DhcpStatus(DhcpProvider.Ours, Loc.T("dhcp.providedByApp", InternetStrings.DhcpLanOnlyNote), dhcpDetail)
                : new DhcpStatus(DhcpProvider.Ours, Loc.T("dhcp.providedByApp2"), dhcpDetail),
            Nat = lanOnly
                ? new NatStatus(NatProvider.LanOnly, InternetStrings.NatLanOnly, true)
                : new NatStatus(NatProvider.Ours, Loc.T("nat.winnat", "VRAirBridgeNat", "192.168.137.0/24"), true),
            UplinkName = Loc.T("common.nameParen", "Ethernet", "Realtek PCIe 2.5GbE Family Controller"),
            Clients = clients,
            Driver = DriverOk(),
            Channel = Channel(goChannel, uplinkChannel: 36),
            Pin = pin ?? new PinStatus(PinPhase.Off, null, null, ChannelStrings.PinOff),
            InternetAllowed = !lanOnly,
        };
    }

    private static HotspotSnapshot Snapshot(HotspotState state, int clients) => new(
        state, DemoSsid, "5 GHz", "WPA2-Personal", "Ethernet", "{6A3F0C1E-0000-4000-8000-0000000000E1}",
        clients, 8, Array.Empty<TetheringClientInfo>(), null, null);

    private static DongleInfo Dongle() => new(
        "WLAN 2", "D-Link DWA-F18 AX3000 USB Adapter", "00-E0-4C-68-00-01", 21, "{6A3F0C1E-0000-4000-8000-000000000021}",
        @"USB\VID_2001&PID_3323\00E04C680001", DriverStrings.BundledVersion, DriverStrings.BundledDate, "D-Link", 1, 0)
    {
        WirelessMode = "802.11a/n/ac/ax",
    };

    private static DriverState DriverOk() => new(DriverStateKind.DriverOk)
    {
        Version = DriverStrings.BundledVersion,
        Date = DriverStrings.BundledDate,
        Provider = "D-Link",
        AdapterName = "WLAN 2",
        DeviceName = "D-Link DWA-F18 AX3000 USB Adapter",
    };

    private static IReadOnlyList<ClientRow> Clients()
    {
        var now = DateTime.Now;
        return new[]
        {
            new ClientRow("2C-26-17-4A-9B-01", "192.168.137.101", "Quest-3", now.AddSeconds(-2), true, "Meta"),
            new ClientRow("5C-E9-1E-12-34-56", "192.168.137.102", "Pixel-8", now.AddSeconds(-14), true, "Google"),
        };
    }

    private static ChannelReport Channel(int? goChannel, int uplinkChannel)
    {
        var neighbors = new List<NeighborBss>
        {
            new(UplinkSsid, "a4:2b:b0:10:20:30", uplinkChannel, 5000 + uplinkChannel * 5, 80, -48, "802.11ax"),
            new("Neighbor-5G", "70:4f:57:aa:bb:cc", 44, 5220, 80, -71, "802.11ac"),
            new("Office-Guest", "10:13:31:de:ad:01", 100, 5500, 80, -80, "802.11ax"),
            new("Printer-Direct", "8a:ee:01:22:33:44", 157, 5785, 20, -84, "802.11n"),
        };
        var candidates = new List<PinCandidate>
        {
            new(UplinkSsid, uplinkChannel, true, true),
            new("Office-Guest", 100, false, true),
            new("HomeWiFi-2G", 6, false, true),
        };
        return new ChannelReport(DateTime.Now.AddMinutes(-2), "Intel(R) Wi-Fi 6E AX211 160MHz", goChannel, goChannel != null ? 80 : null,
            neighbors, ChannelInfo.Occupancy(neighbors), UplinkSsid, uplinkChannel, candidates, null);
    }

    private static IReadOnlyList<LogEntry> LongLogEntries()
    {
        var t = DateTime.Today.AddHours(20).AddMinutes(14);
        var list = new List<LogEntry>(Log(running: false));
        string pool = Loc.T("dhcp.dhcpSettingsUpdatedPool", "192.168.137.100", "192.168.137.199", "DNS 192.168.137.1, 223.5.5.5, 8.8.8.8");
        string beacon = Loc.T("watchdog.beaconCheck", "Intel(R) Wi-Fi 6E AX211 160MHz",
            "149 (5745 MHz) / 80 MHz / 802.11ax / 2 x 2 / RSSI -37 dBm / " + Loc.T("dhcp.listeningReceivedPacketsSentReplies", "192.168.137.1:67", 18, 18));
        for (int i = 0; i < 12; i++)
        {
            var at = t.AddSeconds(10 + i * 30);
            list.Add(new(at, LogLevel.Info, Loc.T("watchdog.userClickedStartHotspot")));
            list.Add(new(at.AddSeconds(1), LogLevel.Ok, Loc.T("watchdog.hotspotStarted")));
            list.Add(new(at.AddSeconds(2), LogLevel.Info, pool));
            list.Add(new(at.AddSeconds(3), i % 3 == 0 ? LogLevel.Warn : LogLevel.Info, Loc.T("watchdog.otherWiFiNetworksSame", 1, UplinkSsid)));
            list.Add(new(at.AddSeconds(4), LogLevel.Ok, beacon));
            list.Add(new(at.AddSeconds(5), LogLevel.Info, Loc.T("nat.icsIsnTWorkingCreating", "VRAirBridgeNat", "192.168.137.0/24")));
            list.Add(new(at.AddSeconds(6), LogLevel.Info, Loc.T("watchdog.deviceConnected", "Quest-3", "192.168.137.101")));
        }
        list.Add(new(t.AddMinutes(7), LogLevel.Ok, NewestLongLogLine));
        return list;
    }

    private static IReadOnlyList<LogEntry> Log(bool running, bool error = false, bool admin = true)
    {
        var t = DateTime.Today.AddHours(20).AddMinutes(14);
        var list = new List<LogEntry>
        {
            new(t, LogLevel.Info, Loc.T("ui.logline.started", Loc.T("app.name"), "1.0.0", Loc.T(admin ? "common.yes" : "common.no"), @"C:\Tools\VRAirBridgeHotspot")),
        };
        if (running)
        {
            list.Add(new(t.AddSeconds(3), LogLevel.Ok, Loc.T("watchdog.hotspotStarted")));
            list.Add(new(t.AddSeconds(41), LogLevel.Info, Loc.T("watchdog.deviceConnected", "Quest-3", "192.168.137.101")));
            list.Add(new(t.AddSeconds(95), LogLevel.Info, Loc.T("watchdog.deviceConnected", "Pixel-8", "192.168.137.102")));
        }
        if (error)
        {
            list.Add(new(t.AddSeconds(5), LogLevel.Warn, Loc.T("watchdog.hotspotUnavailable")));
            list.Add(new(t.AddSeconds(6), LogLevel.Error, Loc.T("ui.opFailed", Loc.T("watchdog.hotspotUnavailable"))));
        }
        return list;
    }
}

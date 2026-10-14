using System.Text;
using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Clients;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core;

/// <summary>一次性收集诊断信息（“VRAirBridgeHotspot.exe --diag” 或界面里“复制诊断信息”）。不改动任何系统设置，不含密码。</summary>
public static class Diagnostics
{
    public static string Collect(AppSettings? s = null, string? exePath = null)
    {
        var sb = new StringBuilder();
        void L(string t) => sb.AppendLine(t);
        static string YesNo(bool? b) => b switch { true => Loc.T("common.yes"), false => Loc.T("common.no"), _ => "?" };
        void Try(string title, Action a)
        {
            try { a(); }
            catch (Exception ex) { L(Loc.T("diag.readFailed", title, ex.Message)); }
        }

        L(Loc.T("diag.vrAirBridgeHotspotDiagnostics", DateTime.Now));
        L(Loc.T("diag.appVersionWindowsAdministrator", AppInfo.Version, Environment.OSVersion.Version, (AdminUtil.IsAdministrator() ? Loc.T("common.yes") : Loc.T("common.no"))));
        L("");

        DongleInfo? dongle = null;
        Try("D-Link", () =>
        {
            dongle = DongleInfo.Find();
            L(Loc.T("diag.dLinkAdapter"));
            if (dongle == null)
            {
                var usb = DongleInfo.FindUsbWithoutDriver();
                L(usb == null ? Loc.T("diag.notFoundUsbVid2001")
                    : Loc.T("diag.hardwarePluggedButHasNo", usb, DongleInfo.NoDriverHint));
                return;
            }
            L(Loc.T("diag.nameDescription", dongle.Name, dongle.Description));
            L(Loc.T("diag.macIfindexStatus", dongle.Mac, dongle.IfIndex, dongle.StatusText));
            L(Loc.T("diag.driver", dongle.DriverText, dongle.DriverProvider));
            L(Loc.T("diag.wirelessMode", dongle.WirelessMode ?? "?"));
            var adv = SystemOptimizer.ReadAdvanced(dongle);
            foreach (var k in new[] { "WifiProtocol_phy0", "bLeisurePs", "InactivePs", "PowerSavingMode", "*WakeOnMagicPacket" })
                if (adv.TryGetValue(k, out var v)) L($"  {k} = {v}");
        });
        L("");

        Try(Loc.T("diag.driver2"), () =>
        {
            L(Loc.T("diag.driverStatus"));
            foreach (var line in DriverInstaller.GetState(detailed: true).DescribeLines()) L("  " + line);
        });
        L("");

        HotspotSnapshot? snap = null;
        Try(Loc.T("diag.hotspot"), () =>
        {
            var hc = new HotspotController();
            if (dongle != null) hc.SetDongle(dongle.Guid, dongle.Mac);
            snap = hc.GetSnapshot();
            L(Loc.T("diag.windowsMobileHotspot"));
            L(Loc.T("diag.stateNameBandSecurity", HotspotController.StateText(snap.State), snap.Ssid, snap.BandText, snap.AuthText));
            L(Loc.T("diag.sharedConnection", snap.UplinkName, snap.UplinkAdapterId));
            L(Loc.T("diag.devices", snap.ClientCount, snap.MaxClients));
            foreach (var c in snap.Clients) L($"    {c.Mac}  {string.Join(", ", c.HostNames)}");
            if (snap.CapabilityProblem != null) L(Loc.T("diag.capabilityProblem") + snap.CapabilityProblem);
            if (snap.Error != null) L(Loc.T("diag.error") + snap.Error);
            // the registry override PeerlessTimeoutEnabled=0 wins over the Windows setting: say which one is in effect
            var winSetting = HotspotController.IsNoConnectionsTimeoutEnabled() switch { true => Loc.T("diag.text"), false => Loc.T("diag.off"), _ => "?" };
            L(SystemOptimizer.IsPeerlessTimeoutDisabled()
                ? Loc.T("diag.autoOffOverridden", winSetting)
                : Loc.T("diag.autoOffWhenNoDevices", winSetting));
        });
        L("");

        GoAdapterInfo? go = null;
        Try("GO", () =>
        {
            go = NetworkInfo.FindGoAdapter(dongle?.Mac);
            L(Loc.T("diag.hotspotAdapterWiFiDirect"));
            if (go == null) { L(Loc.T("diag.noneHotspotOff")); return; }
            L($"  {go.Name} / {go.Description}");
            L($"  MAC: {go.Mac}  ifIndex: {go.IfIndex}  IP: {go.Ip}/{(go.Mask != null ? NetworkInfo.PrefixLength(go.Mask) : 0)}");
            L(Loc.T("diag.hotspotDLink", (dongle == null ? Loc.T("diag.noDLink") : MacUtil.SameRadio(go.Mac, dongle.Mac) ? Loc.T("common.yes") : Loc.T("common.no"))));
            L(Loc.T("diag.ipv4Forwarding", YesNo(NatManager.GetForwarding(go.IfIndex))));
        });
        L("");

        Try("WLAN log", () =>
        {
            // who tried to join and where it stopped (wrong password / dropped / no IP), from the WLAN AutoConfig log
            const int hours = 12;
            L(Loc.T("join.diagTitle", hours));
            var evs = ClientJoinMonitor.ReadRecent(TimeSpan.FromHours(hours), out var err);
            if (err != null) { L("  " + Loc.T("join.monitorUnavailable", err)); return; }
            var filter = new HotspotInterfaceFilter { AnyWifiDirect = true };
            if (go != null) filter.Add(go.Id, go.Mac);
            var (findings, _) = ClientJoinMonitor.Replay(evs, filter, DateTime.UtcNow);
            if (findings.Count == 0) L(Loc.T("join.diagNone"));
            foreach (var f in findings.TakeLast(25)) L("  " + JoinText.Describe(f));
            var timeline = ClientJoinMonitor.Timeline(evs, filter, 60);
            if (timeline.Count > 0)
            {
                L("  --");
                foreach (var line in timeline) L("  " + line);
            }
        });
        L("");

        Try(Loc.T("diag.uplink"), () =>
        {
            var up = NetworkInfo.GetUplink(snap?.UplinkAdapterId);
            L(Loc.T("diag.internetConnection"));
            if (up == null) { L("  ?"); return; }
            L(Loc.T("diag.ipGatewayDns", up.Name, up.Description, up.Ip, up.Gateway, string.Join(", ", up.Dns)));
            L(Loc.T("diag.ipv4Forwarding", YesNo(NatManager.GetForwarding(up.IfIndex))));
        });
        Try(Loc.T("diag.uplink"), () =>
        {
            // all candidate connections and why each one was (not) chosen as the uplink
            var probe = UplinkProbe.Probe(dongle?.Guid, dongle?.Mac);
            var win = probe.Entries.Select(e => e.Candidate).FirstOrDefault(c => UplinkSelector.SameAdapter(c.AdapterId, probe.WindowsDefaultAdapterId));
            L(Loc.T("diag.uplinkCandidates", win == null ? "-" : win.AdapterName.Length > 0 ? win.AdapterName : win.ProfileName));
            foreach (var c in UplinkSelector.Rank(probe.Entries.Select(e => e.Candidate)))
            {
                var x = UplinkSelector.Exclusion(c, out var marker);
                var verdict = UplinkSelector.SameAdapter(c.AdapterId, snap?.UplinkAdapterId) ? Loc.T("diag.uplinkChosen")
                    : x == UplinkExclusion.None ? Loc.T("diag.uplinkEligible")
                    : Loc.T("diag.uplinkExcluded", HotspotController.ExclusionText(x, marker));
                L(Loc.T("diag.uplinkCandidate", c.AdapterName.Length > 0 ? c.AdapterName : "?", c.ProfileName.Length > 0 ? c.ProfileName : "?",
                    c.Connectivity, YesNo(c.HasIPv4Gateway), c.Metric?.ToString() ?? "-", verdict));
            }
        });
        L("");

        Try(Loc.T("diag.ports"), () =>
        {
            L(Loc.T("diag.udp6753Listeners"));
            foreach (var l in IpHelper.GetUdpListeners().Where(l => l.Port is 67 or 53).OrderBy(l => l.Port))
                L($"  {l.Address}:{l.Port}  PID {l.Pid}  [{NetworkInfo.AdapterNameForIp(l.Address) ?? "-"}]");
        });
        L("");

        Try("NAT", () =>
        {
            L("== NetNat ==");
            var nats = NatManager.GetNats();
            if (nats.Count == 0) L(Loc.T("diag.none"));
            foreach (var n in nats) L($"  {n.Name}  {n.InternalPrefix}  Active={n.Active}");
        });
        L("");

        Try("ICS", () =>
        {
            L(Loc.T("diag.icsEntries"));
            var recs = IcsRepair.ReadRecords();
            if (recs.Count == 0) L(Loc.T("diag.none"));
            foreach (var r in recs) L($"  {(r.IsPublic ? Loc.T("diag.public") : Loc.T("diag.private"))}: {r.Name}  {(r.AdapterExists ? "" : Loc.T("diag.adapterNoLongerExistsGhost"))}");
            var ds = IcsRepair.FindEnabledDefaultSwitch();
            L($"  Hyper-V Default Switch: {(ds == null ? Loc.T("diag.doesnTExistOrDisabled") : ds + Loc.T("diag.enabled"))}");
            var svc = ServiceUtil.Get("SharedAccess");
            if (svc != null) L(Loc.T("diag.sharedaccessServicePid", svc.State, svc.StartMode, svc.ProcessId));
            var ovr = ServiceUtil.Get("OVRService");
            if (ovr != null) L(Loc.T("diag.ovrserviceMeta", ovr.State, ovr.StartMode));
        });
        L("");

        Try(Loc.T("diag.firewall"), () =>
        {
            L(Loc.T("diag.firewallStartWithWindows"));
            var exe = exePath ?? Environment.ProcessPath ?? "";
            var fw = FirewallManager.Check(exe);
            L(Loc.T("diag.udp67RuleProgramRule", (fw.DhcpRuleOk ? Loc.T("diag.yes") : Loc.T("diag.none2")), (fw.AppRuleOk ? Loc.T("diag.yes") : Loc.T("diag.none2")), (fw.BlockingRules.Count == 0 ? Loc.T("diag.none2") : string.Join(", ", fw.BlockingRules))) + (fw.Error != null ? Loc.T("diag.error") + fw.Error : ""));
            L(Loc.T("diag.startWithWindowsTask", (AutoStartManager.IsEnabled(out var p) ? Loc.T("diag.yes2") + p : Loc.T("diag.none2"))));
        });
        L("");

        Try("WLAN", () =>
        {
            L(Loc.T("diag.wlanInterfacesBeaconScans"));
            foreach (var w in BeaconChecker.EnumInterfaces()) L($"  {w.Description}  {w.Guid}  state={w.State}");
        });
        L("");

        Try(Loc.T("diag.channel"), () =>
        {
            L(Loc.T("diag.channel2"));
            var report = ChannelInfo.Last;
            if (report == null)
            {
                // 只读扫描：热点开着时不用 D-Link 扫（会打断串流）
                bool hotspotOn = snap?.State == HotspotState.On;
                var scan = Task.Run(() => ChannelInfo.ScanAsync(go?.Mac, dongle?.Guid, allowDongle: !hotspotOn, s?.Ssid));
                report = scan.Wait(TimeSpan.FromSeconds(20)) ? scan.Result : null;
            }
            foreach (var line in ChannelInfo.DescribeLines(report)) L("  " + line);
            if (s != null)
            {
                var pin = (s.Channel.PinSsid ?? "").Trim();
                L($"  {ChannelStrings.PinLabel}: {(pin.Length == 0 ? ChannelStrings.PinAuto : pin)}" +
                  (s.Revert.PinProfilesAdded.Count > 0 ? Loc.T("diag.profilesCopiedDLink", s.Revert.PinProfilesAdded.Count) : ""));
            }
        });
        L("");

        Try("Meta", () =>
        {
            L("== " + MetaStrings.Section + " ==");
            foreach (var line in MetaLeftovers.Scan().DescribeLines()) L("  " + line);
        });

        if (s != null)
        {
            L("");
            L(Loc.T("diag.settingsPasswordExcluded"));
            L(Loc.T("diag.nameBandSecurityDhcpNat", (string.IsNullOrEmpty(s.Ssid) ? Loc.T("diag.notSetYetGeneratedFirst") : s.Ssid), s.Band, s.Auth, s.Dhcp.Mode, s.Nat.Mode, YesNo(s.AutoStart), YesNo(s.StartHotspotOnLaunch)));
            L(Loc.T("diag.internetAllow", InternetStrings.StatusText(s.Internet.Allow), (s.Internet.Allow ? "true" : "false")));
        }
        return sb.ToString();
    }
}

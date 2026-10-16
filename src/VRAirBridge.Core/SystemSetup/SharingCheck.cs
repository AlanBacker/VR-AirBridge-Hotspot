using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.Core.SystemSetup;

/// <summary>IPv4 forwarding of one adapter (Enabled = null: could not be read).</summary>
public sealed record ForwardingState(string Name, int IfIndex, bool? Enabled);

/// <summary>
/// What the hotspot left behind after it stopped: Windows ICS still sharing an adapter, or IPv4 forwarding still on.
/// Either one keeps full-tunnel VPNs (WireGuard...) from connecting. Checked lists the adapters whose forwarding was read.
/// </summary>
public sealed record SharingReport(IReadOnlyList<IcsRecord> IcsShared, IReadOnlyList<ForwardingState> ForwardingOn,
    IReadOnlyList<string> Checked, IReadOnlyList<string> OnByApp)
{
    public bool Clean => IcsShared.Count == 0 && ForwardingOn.Count == 0;

    private bool ByApp(ForwardingState f) => OnByApp.Contains(f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>One line per thing left on.</summary>
    public List<string> ProblemLines()
    {
        var lines = new List<string>();
        foreach (var r in IcsShared)
            lines.Add(Loc.T("sharing.icsOn", r.Name, Loc.T(r.IsPublic ? "sharing.icsPublic" : "sharing.icsPrivate")));
        foreach (var f in ForwardingOn)
            lines.Add(Loc.T(ByApp(f) ? "sharing.forwardingOnByApp" : "sharing.forwardingOn", f.Name));
        return lines;
    }

    /// <summary>What it breaks and how to undo it.</summary>
    public List<string> AdviceLines()
    {
        var lines = new List<string> { Loc.T("sharing.vpnNote") };
        if (ForwardingOn.Any(ByApp)) lines.Add(Loc.T("sharing.adviceRevert"));
        if (IcsShared.Count > 0 || ForwardingOn.Any(f => !ByApp(f))) lines.Add(Loc.T("sharing.adviceRestart"));
        return lines;
    }

    /// <summary>"Checked: ICS is off and IPv4 forwarding is Disabled on WLAN".</summary>
    public string OkLine() => Checked.Count == 0
        ? Loc.T("sharing.okIcsOnly")
        : Loc.T("sharing.ok", string.Join(Loc.T("common.enumSep"), Checked));

    /// <summary>Notification / hint text: what is left, then the advice.</summary>
    public string Text() => string.Join("\n", ProblemLines().Concat(AdviceLines()));
}

/// <summary>
/// Read-only check that Windows ICS has stopped sharing and IPv4 forwarding is off again (run after the hotspot stops and
/// after Revert). Reads WMI (HNet_ConnectionProperties, MSFT_NetIPInterface); needs no administrator rights and changes nothing.
/// </summary>
public static class SharingCheck
{
    /// <summary>
    /// Pure part. ics = ICS records (only adapters that still exist count; ghost records are Repair's business);
    /// forwarding = adapters read; onByApp = adapters this app turned forwarding on for (revert.forwardingEnabledOn).
    /// </summary>
    public static SharingReport Analyze(IEnumerable<IcsRecord> ics, IEnumerable<ForwardingState> forwarding, IEnumerable<string> onByApp)
    {
        var fw = forwarding.ToList();
        return new SharingReport(
            ics.Where(r => r.AdapterExists && (r.IsPublic || r.IsPrivate)).ToList(),
            fw.Where(f => f.Enabled == true).ToList(),
            fw.Where(f => f.Enabled != null).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            onByApp.ToList());
    }

    /// <summary>
    /// The adapters whose forwarding matters: every up physical Ethernet / Wi-Fi adapter (the uplink), plus any adapter this app
    /// turned forwarding on for. Never the Wi-Fi Direct hotspot adapter (it goes away with the hotspot).
    /// </summary>
    public static List<NetAdapterSample> AdaptersToCheck(IEnumerable<NetAdapterSample> adapters, IEnumerable<string> onByApp)
    {
        var byApp = new HashSet<string>(onByApp, StringComparer.OrdinalIgnoreCase);
        return adapters
            .Where(a => a.IfIndex > 0 && !UplinkSelector.IsWifiDirectDescription(a.Description))
            .Where(a => (a.IsUp && VpnDetector.IsPhysical(a)) || byApp.Contains(a.Name))
            .GroupBy(a => a.IfIndex).Select(g => g.First())
            .ToList();
    }

    public static SharingReport Check(AppSettings s)
    {
        var onByApp = s.Revert.ForwardingEnabledOn.ToList();
        var fw = AdaptersToCheck(VpnDetector.SampleAdapters(), onByApp)
            .Select(a => new ForwardingState(a.Name, a.IfIndex, NatManager.GetForwarding(a.IfIndex)));
        return Analyze(IcsRepair.ReadRecords(), fw, onByApp);
    }

    /// <summary>
    /// Windows takes a few seconds to tear sharing down after the hotspot stops: checks every interval until clean or until
    /// timeout, and returns the last report. Returns null as soon as abort() says the result no longer matters (hotspot started again).
    /// </summary>
    public static async Task<SharingReport?> WaitUntilCleanAsync(Func<SharingReport> check, TimeSpan timeout, TimeSpan interval,
        Func<bool>? abort = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (abort?.Invoke() == true) return null;
            var rep = check();
            if (rep.Clean || DateTime.UtcNow + interval > deadline) return abort?.Invoke() == true ? null : rep;
            await Task.Delay(interval, ct);
        }
    }
}

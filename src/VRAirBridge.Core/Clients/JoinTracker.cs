using VRAirBridge.Core.Net;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Clients;

/// <summary>
/// Follows every device through joining the hotspot and explains failed joins.
/// Input: WLAN AutoConfig events of the hotspot interface (<see cref="OnEvent"/>, any thread) and, once per watchdog
/// tick, the IP evidence (<see cref="Tick"/>). Output: one <see cref="ClientJoinRecord"/> per device and
/// <see cref="JoinFinding"/>s for (a) password check failed, (b) dropped right after the password check,
/// (c) no IP address, (d) reconnect loop and (e) successful joins.
/// <para>
/// Events 11010 / 20020 / 11005 / 11004 carry no peer MAC: they are matched by ConnectionId (learned from the 11010
/// that follows a 20019) or, failing that, to the most recent association on the same interface.
/// </para>
/// All times are UTC. Thread-safe.
/// </summary>
public sealed class JoinTracker
{
    /// <summary>(a) Associated but the password check did not finish within this time.</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(8);
    /// <summary>
    /// (a) A healthy password check takes ~10 ms (at most ~0.5 s in the logs). Still unfinished after this long when the
    /// hotspot is stopped / restarted: judged as failed too (the stop came before <see cref="AuthTimeout"/>).
    /// </summary>
    public static readonly TimeSpan HandshakeStall = TimeSpan.FromSeconds(3);
    /// <summary>(b) Left within this time after the password check, before any DHCP request reached this app.</summary>
    public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(5);
    /// <summary>(b) Same, when the DHCP traffic is not visible (Windows ICS / replay): only near-instant drops count.</summary>
    public static readonly TimeSpan DropWindowBlind = TimeSpan.FromSeconds(2);
    /// <summary>(c) No DHCP lease from this app within this time after the password check.</summary>
    public static readonly TimeSpan OurDhcpTimeout = TimeSpan.FromSeconds(15);
    /// <summary>(c) No neighbor entry (Windows ICS / unknown DHCP) within this time after the password check.</summary>
    public static readonly TimeSpan OtherDhcpTimeout = TimeSpan.FromSeconds(20);
    /// <summary>(d) Window for counting reconnect cycles.</summary>
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(5);
    public const int LoopCycles = 3;
    /// <summary>At most one finding per device and failure kind in this time.</summary>
    public static readonly TimeSpan RateLimit = TimeSpan.FromMinutes(5);
    /// <summary>A leave is judged this long after it happened (hotspot-stop events may still follow).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    /// <summary>11010 / 20020 / 11005 belong to a 20019 at most this much earlier.</summary>
    public static readonly TimeSpan Correlate = TimeSpan.FromSeconds(5);
    /// <summary>A leave this close to a hotspot stop is caused by the stop, not by the device.</summary>
    public static readonly TimeSpan StopWindow = TimeSpan.FromSeconds(4);
    /// <summary>Devices not seen for this long are forgotten.</summary>
    public static readonly TimeSpan Forget = TimeSpan.FromHours(2);
    /// <summary>Keep this many findings for diagnostics.</summary>
    public const int HistorySize = 30;

    private sealed class Attempt
    {
        public DateTime Start;
        public DateTime? AuthAt;
        public DateTime? LeftAt;
        public DateTime? IpAt;
        public string? ConnectionId;
        public bool Synthetic;
        /// <summary>Synthetic attempt while events are available (client list may lag): never reports failures.</summary>
        public bool Quiet;
        public bool StopRelated;
        public bool Judged;
        public bool NoIpReported;
        public bool LeftByTimeout;
    }

    private sealed class Device
    {
        public string Mac = "";
        public string? Guid;
        public DateTime FirstSeen;
        public DateTime LastChange;
        public JoinStage Stage = JoinStage.Left;
        public string? Ip;
        public string? HostName;
        public JoinFailure? Failure;
        public Attempt? Current;
        public readonly List<Attempt> Closing = new();
        public readonly List<DateTime> FailedCycles = new();
        public int Attempts;
        public DateTime? LastPresent;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Device> _dev = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _conn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Mac, JoinFailure Failure), DateTime> _reported = new();
    private readonly List<JoinFinding> _history = new();
    private DateTime? _lastStop;
    private DateTime? _liveSince;

    /// <summary>True while WLAN events are being received. False = only the Windows client list is used (no (a)/(b)).</summary>
    public bool EventsAvailable { get; set; }

    /// <summary>Events before this time came from the event-log backlog (their findings are marked FromBacklog).</summary>
    public void MarkLive(DateTime utc) { lock (_lock) _liveSince = utc; }

    /// <summary>Feeds one event of the hotspot interface (already filtered). Events must arrive in time order.</summary>
    public void OnEvent(WlanEvent e)
    {
        lock (_lock)
        {
            var t = e.TimeUtc;
            switch (e.Kind)
            {
                case WlanEventKind.Associated:
                {
                    if (e.PeerMac.Length != 12) return;
                    var d = Get(e.PeerMac, t);
                    d.Guid = e.InterfaceGuid ?? d.Guid;
                    StartAttempt(d, t, synthetic: false);
                    break;
                }
                case WlanEventKind.SecurityStarted:
                {
                    var d = RecentAssociation(e, needUnauthenticated: true);
                    if (d?.Current == null) return;
                    if (!string.IsNullOrEmpty(e.ConnectionId))
                    {
                        _conn[e.ConnectionId] = d.Mac;
                        d.Current.ConnectionId = e.ConnectionId;
                    }
                    break;
                }
                case WlanEventKind.Authenticated:
                case WlanEventKind.SecuritySucceeded:
                {
                    var d = ByConnection(e) ?? RecentAssociation(e, needUnauthenticated: true);
                    var a = d?.Current;
                    if (d == null || a == null || a.LeftAt != null) return;
                    if (a.AuthAt == null)
                    {
                        a.AuthAt = t;
                        if (d.Stage == JoinStage.Associated) { d.Stage = JoinStage.Authenticated; d.LastChange = t; }
                    }
                    break;
                }
                case WlanEventKind.SecurityStopped:
                {
                    var d = ByConnection(e) ?? LeavingDevice(e);
                    if (d?.Current == null || d.Current.LeftAt != null) return;
                    Leave(d, t);
                    break;
                }
                case WlanEventKind.HotspotStopping:
                case WlanEventKind.GoDisconnected:
                    if (e.Kind == WlanEventKind.GoDisconnected && e.ProfileName != null &&
                        !e.ProfileName.Equals(WlanEvent.GroupOwnerProfile, StringComparison.OrdinalIgnoreCase)) return;
                    StopAll(t);
                    break;
                case WlanEventKind.HotspotStarting:
                case WlanEventKind.HotspotStarted:
                    _conn.Clear();
                    break;
            }
        }
    }

    /// <summary>The hotspot went off (seen by the watchdog): every device left because of the stop.</summary>
    public void HotspotStopped(DateTime utc)
    {
        lock (_lock) StopAll(utc);
    }

    /// <summary>Applies the IP evidence of this tick, judges timeouts and leaves. Returns the new findings (rate-limited).</summary>
    public List<JoinFinding> Tick(DateTime now, JoinContext ctx)
    {
        var found = new List<JoinFinding>();
        lock (_lock)
        {
            ApplyPresence(now, ctx);
            foreach (var d in _dev.Values.ToList())
            {
                // names
                if (ctx.Leases.TryGetValue(d.Mac, out var lease) && lease.HostName != null) d.HostName = lease.HostName;
                else if (ctx.HostNames.TryGetValue(d.Mac, out var hn)) d.HostName = hn;

                // closed attempts (left, waiting for Settle)
                foreach (var a in d.Closing.ToList())
                {
                    if (a.LeftAt != null && now - a.LeftAt.Value < Settle) continue;
                    d.Closing.Remove(a);
                    Judge(d, a, now, ctx, found);
                }

                var cur = d.Current;
                if (cur == null || cur.LeftAt != null) continue;

                // got an IP?
                if (cur.IpAt == null && cur.AuthAt != null && HasIp(d, cur, ctx, now, out var ip))
                {
                    cur.IpAt = now;
                    d.Ip = ip;
                    d.Stage = JoinStage.GotIp;
                    d.Failure = null;
                    d.FailedCycles.Clear();
                    d.LastChange = now;
                    if (!cur.Judged)
                    {
                        cur.Judged = true;
                        Add(found, Finding(d, cur, null, now, ctx));
                    }
                    continue;
                }
                if (d.Stage == JoinStage.GotIp && HasIp(d, cur, ctx, now, out var ip2)) d.Ip = ip2;

                // (a) password check never finished (unless Windows lists the device as connected: the event was missed)
                if (cur.AuthAt == null && now - cur.Start >= AuthTimeout && ctx.Present?.Contains(d.Mac) == true)
                {
                    cur.AuthAt = now;
                    d.Stage = JoinStage.Authenticated;
                    d.LastChange = now;
                }
                if (cur.AuthAt == null && now - cur.Start >= AuthTimeout)
                {
                    cur.LeftAt = now;
                    cur.LeftByTimeout = true;
                    d.Stage = JoinStage.Left;
                    d.LastChange = now;
                    d.Current = null;
                    Judge(d, cur, now, ctx, found);
                    continue;
                }

                // (c) authenticated, still connected, no IP
                if (cur.AuthAt != null && cur.IpAt == null && !cur.NoIpReported && !cur.Quiet && ctx.JudgeNoIp &&
                    now - cur.AuthAt.Value >= (ctx.Dhcp == JoinDhcp.Ours ? OurDhcpTimeout : OtherDhcpTimeout))
                {
                    cur.NoIpReported = true;
                    d.Failure = JoinFailure.NoIp;
                    var f = Finding(d, cur, JoinFailure.NoIp, now, ctx);
                    Add(found, f);
                }
            }

            // forget old devices
            foreach (var d in _dev.Values.Where(d => d.Stage == JoinStage.Left && now - d.LastChange > Forget && d.Closing.Count == 0).ToList())
                _dev.Remove(d.Mac);
            foreach (var k in _reported.Where(kv => now - kv.Value > RateLimit).Select(kv => kv.Key).ToList())
                _reported.Remove(k);
        }
        return found;
    }

    /// <summary>All known devices, most recent first.</summary>
    public List<ClientJoinRecord> Snapshot()
    {
        lock (_lock) return _dev.Values.OrderByDescending(d => d.LastChange).Select(ToRecord).ToList();
    }

    /// <summary>Findings so far (oldest first, at most <see cref="HistorySize"/>).</summary>
    public List<JoinFinding> History()
    {
        lock (_lock) return _history.ToList();
    }

    public ClientJoinRecord? Get(string mac)
    {
        lock (_lock) return _dev.TryGetValue(MacUtil.Normalize(mac), out var d) ? ToRecord(d) : null;
    }

    // ------------------------------------------------------------------ internals (under _lock)

    private Device Get(string mac, DateTime t)
    {
        if (!_dev.TryGetValue(mac, out var d))
        {
            d = new Device { Mac = mac, FirstSeen = t, LastChange = t };
            _dev[mac] = d;
        }
        return d;
    }

    private void StartAttempt(Device d, DateTime t, bool synthetic)
    {
        if (d.Current != null && d.Current.LeftAt == null)
        {
            // re-associated before the previous attempt ended: that attempt is over now
            d.Current.LeftAt = t;
            d.Closing.Add(d.Current);
        }
        d.Current = new Attempt { Start = t, Synthetic = synthetic, AuthAt = synthetic ? t : null };
        d.Stage = synthetic ? JoinStage.Authenticated : JoinStage.Associated;
        d.Ip = null;
        d.LastChange = t;
        d.Attempts++;
    }

    private void Leave(Device d, DateTime t)
    {
        var a = d.Current;
        if (a == null) return;
        a.LeftAt = t;
        if (_lastStop != null && (t - _lastStop.Value).Duration() <= StopWindow) a.StopRelated = true;
        d.Closing.Add(a);
        d.Current = null;
        d.Stage = JoinStage.Left;
        d.LastChange = t;
    }

    private void StopAll(DateTime t)
    {
        _lastStop = t;
        foreach (var d in _dev.Values)
        {
            foreach (var a in d.Closing)
                if (a.LeftAt != null && (t - a.LeftAt.Value).Duration() <= StopWindow) a.StopRelated = true;
            if (d.Current != null && d.Current.LeftAt == null)
            {
                d.Current.StopRelated = true;
                Leave(d, t);
            }
        }
        _conn.Clear();
    }

    private Device? ByConnection(WlanEvent e)
    {
        if (string.IsNullOrEmpty(e.ConnectionId) || !_conn.TryGetValue(e.ConnectionId, out var mac)) return null;
        return _dev.TryGetValue(mac, out var d) && d.Current != null && d.Current.ConnectionId == e.ConnectionId ? d : null;
    }

    /// <summary>The device whose association (20019) is the latest one on this interface within <see cref="Correlate"/>.</summary>
    private Device? RecentAssociation(WlanEvent e, bool needUnauthenticated)
    {
        Device? best = null;
        foreach (var d in _dev.Values)
        {
            var a = d.Current;
            if (a == null || a.Synthetic || a.LeftAt != null) continue;
            if (needUnauthenticated && a.AuthAt != null) continue;
            if (e.TimeUtc - a.Start > Correlate || a.Start - e.TimeUtc > TimeSpan.FromSeconds(1)) continue;
            if (!SameInterface(d, e)) continue;
            if (best == null || a.Start > best.Current!.Start) best = d;
        }
        return best;
    }

    /// <summary>For 11004 without a known ConnectionId: a device still in the password step, else the only connected one.</summary>
    private Device? LeavingDevice(WlanEvent e)
    {
        var active = _dev.Values.Where(d => d.Current != null && d.Current.LeftAt == null && !d.Current.Synthetic && SameInterface(d, e)).ToList();
        var pending = active.Where(d => d.Current!.AuthAt == null && e.TimeUtc - d.Current.Start <= TimeSpan.FromSeconds(30))
                            .OrderByDescending(d => d.Current!.Start).FirstOrDefault();
        if (pending != null) return pending;
        return active.Count == 1 ? active[0] : null;
    }

    private static bool SameInterface(Device d, WlanEvent e) =>
        d.Guid == null || e.InterfaceGuid == null || string.Equals(Trim(d.Guid), Trim(e.InterfaceGuid), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string g) => g.Trim().Trim('{', '}');

    /// <summary>Uses the Windows client list: devices connected without an event (event log unavailable / joined before the backlog) and silent leaves.</summary>
    private void ApplyPresence(DateTime now, JoinContext ctx)
    {
        if (ctx.Present == null) return;
        foreach (var mac in ctx.Present)
        {
            var d = Get(mac, now);
            d.LastPresent = now;
            bool idle = d.Current == null || d.Current.LeftAt != null;
            if (!idle) continue;
            var lastLeave = d.Closing.Select(a => a.LeftAt).Concat(new[] { d.Stage == JoinStage.Left ? (DateTime?)d.LastChange : null }).Max();
            bool fresh = d.Attempts == 0;
            if (!EventsAvailable || fresh || lastLeave == null || now - lastLeave.Value > TimeSpan.FromSeconds(10))
            {
                StartAttempt(d, now, synthetic: true);
                d.Current!.Quiet = EventsAvailable && !fresh;
            }
        }
        foreach (var d in _dev.Values)
        {
            var a = d.Current;
            if (a == null || a.LeftAt != null || ctx.Present.Contains(d.Mac)) continue;
            // listed before, now gone for a while: left without an event
            var since = d.LastPresent ?? (a.Synthetic ? a.Start : (DateTime?)null);
            if (since != null && now - since.Value > TimeSpan.FromSeconds(20) && (a.AuthAt != null || a.Synthetic))
                Leave(d, now - Settle);
        }
    }

    private static bool HasIp(Device d, Attempt a, JoinContext ctx, DateTime now, out string? ip)
    {
        ip = null;
        var from = a.Start - TimeSpan.FromSeconds(2);
        var until = (a.LeftAt ?? now) + TimeSpan.FromSeconds(2);
        if (ctx.Leases.TryGetValue(d.Mac, out var l) && l.LastSeenUtc >= from && l.LastSeenUtc <= until && (l.Active || a.LeftAt != null))
        {
            ip = l.Ip;
            return true;
        }
        if (ctx.Dhcp == JoinDhcp.Ours && !a.Synthetic) return false;
        // a fresh entry for a device that just joined; any usable entry for one that was already connected
        if (ctx.Neighbors.TryGetValue(d.Mac, out var n) && !NetworkInfo.IsApipa(n.Address) &&
            (n.IsFresh || (a.Synthetic && n.State == NeighborState.Stale)))
        {
            ip = n.Address.ToString();
            return true;
        }
        if (ctx.Dhcp == JoinDhcp.Ours && ctx.Leases.TryGetValue(d.Mac, out var l2) && l2.Active)
        {
            // synthetic attempt (no association event): an active lease is enough
            ip = l2.Ip;
            return true;
        }
        return false;
    }

    private static bool DhcpSeen(Device d, Attempt a, JoinContext ctx, DateTime now) =>
        ctx.DhcpPackets.TryGetValue(d.Mac, out var t) && t >= a.Start - TimeSpan.FromSeconds(2) && t <= (a.LeftAt ?? now) + TimeSpan.FromSeconds(2);

    private void Judge(Device d, Attempt a, DateTime now, JoinContext ctx, List<JoinFinding> found)
    {
        if (a.IpAt != null) return;
        if (HasIp(d, a, ctx, now, out var ip) && a.AuthAt != null)
        {
            // got its address between two ticks
            a.IpAt = a.LeftAt ?? now;
            d.Ip = ip;
            d.Failure = null;
            d.FailedCycles.Clear();
            if (!a.Judged)
            {
                a.Judged = true;
                Add(found, Finding(d, a, null, a.IpAt.Value, ctx));
            }
            return;
        }
        if (a.Quiet) { a.Judged = true; return; }
        if (a.StopRelated)
        {
            // a stop cuts every connection; only a password check that had already stalled is still a failure
            bool stalled = a.AuthAt == null && !a.Synthetic && a.LeftAt != null && a.LeftAt.Value - a.Start >= HandshakeStall;
            if (!stalled) { a.Judged = true; return; }
        }

        JoinFailure? failure = null;
        if (a.AuthAt == null) failure = JoinFailure.WrongPassword;
        else if (a.LeftAt != null && a.LeftAt.Value - a.AuthAt.Value <= (ctx.Dhcp == JoinDhcp.Ours ? DropWindow : DropWindowBlind) && !DhcpSeen(d, a, ctx, now)) failure = JoinFailure.DroppedAfterAuth;
        else if (!a.NoIpReported && ctx.JudgeNoIp) failure = JoinFailure.NoIp;
        else if (a.NoIpReported) failure = JoinFailure.NoIp;

        if (failure != null)
        {
            d.Failure = failure;
            if (!(failure == JoinFailure.NoIp && a.NoIpReported))
            {
                a.NoIpReported |= failure == JoinFailure.NoIp;
                Add(found, Finding(d, a, failure, a.LeftAt ?? now, ctx));
            }
        }
        a.Judged = true;

        // (d) authenticated but no IP, over and over (replays without IP information: only short connections count)
        if (a.AuthAt != null && (ctx.JudgeNoIp || (a.LeftAt ?? now) - a.Start <= TimeSpan.FromSeconds(120)))
        {
            d.FailedCycles.Add(a.Start);
            d.FailedCycles.RemoveAll(s => a.Start - s > LoopWindow);
            if (d.FailedCycles.Count >= LoopCycles)
            {
                var first = d.FailedCycles[0];
                int n = d.FailedCycles.Count;
                d.Failure = JoinFailure.ReconnectLoop;
                Add(found, Finding(d, a, JoinFailure.ReconnectLoop, a.LeftAt ?? now, ctx), n,
                    (int)Math.Round((a.Start - first).TotalSeconds / (n - 1)));
            }
        }
    }

    private JoinFinding Finding(Device d, Attempt a, JoinFailure? failure, DateTime t, JoinContext ctx)
    {
        var end = failure == null ? (a.IpAt ?? t) : (a.LeftAt ?? t);
        return new JoinFinding(ToRecord(d), failure, t)
        {
            OurDhcp = ctx.Dhcp == JoinDhcp.Ours,
            Dhcp = ctx.Dhcp,
            FromBacklog = _liveSince != null && a.Start < _liveSince.Value,
            DhcpRequestsSeen = DhcpSeen(d, a, ctx, t),
            Wpa3 = ctx.Wpa3,
            ElapsedSeconds = Math.Max(0, (int)Math.Round((end - a.Start).TotalSeconds)),
            AttemptUtc = a.Start,
        };
    }

    private void Add(List<JoinFinding> found, JoinFinding f, int cycles = 0, int interval = 0)
    {
        if (cycles > 0) f = f with { Cycles = cycles, IntervalSeconds = interval };
        if (f.Failure is { } kind)
        {
            var key = (MacUtil.Normalize(f.Record.Mac), kind);
            if (_reported.TryGetValue(key, out var last) && f.TimeUtc - last < RateLimit && f.TimeUtc >= last) return;
            _reported[key] = f.TimeUtc;
        }
        found.Add(f);
        _history.Add(f);
        if (_history.Count > HistorySize) _history.RemoveRange(0, _history.Count - HistorySize);
    }

    private static ClientJoinRecord ToRecord(Device d) => new(
        MacUtil.Format(d.Mac), d.FirstSeen, d.LastChange, d.Stage, d.Stage == JoinStage.Left ? null : d.Ip, d.HostName, d.Failure, d.Attempts);
}

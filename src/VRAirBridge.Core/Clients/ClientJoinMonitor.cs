using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;

namespace VRAirBridge.Core.Clients;

/// <summary>
/// Reads the WLAN AutoConfig operational log (readable without administrator rights) and feeds the events of the
/// hotspot interface to a <see cref="JoinTracker"/>: first the backlog since a given time, then live events.
/// </summary>
public sealed class ClientJoinMonitor : IDisposable
{
    public const string LogName = "Microsoft-Windows-WLAN-AutoConfig/Operational";

    private readonly JoinTracker _tracker;
    private readonly HotspotInterfaceFilter _filter;
    private readonly object _lock = new();
    private EventLogWatcher? _watcher;

    public ClientJoinMonitor(JoinTracker tracker, HotspotInterfaceFilter? filter = null)
    {
        _tracker = tracker;
        _filter = filter ?? new HotspotInterfaceFilter();
    }

    /// <summary>Raised (on a thread-pool thread) after an event of the hotspot interface was passed to the tracker.</summary>
    public event Action<WlanEvent>? EventReceived;

    public bool Running { get { lock (_lock) return _watcher != null; } }

    /// <summary>Why the monitor could not start / stopped receiving events; null = fine.</summary>
    public string? Error { get; private set; }

    /// <summary>Adds the current hotspot (GO) adapter: its interface GUID and MAC.</summary>
    public void AddInterface(string? guid, string? mac) => _filter.Add(guid, mac);

    /// <summary>Starts reading: the backlog since <paramref name="backlogSinceUtc"/>, then live events. Returns false on failure (see <see cref="Error"/>).</summary>
    public bool Start(DateTime backlogSinceUtc)
    {
        lock (_lock)
        {
            if (_watcher != null) return true;
            try
            {
                var q = new EventLogQuery(LogName, PathType.LogName, BuildXPath(backlogSinceUtc));
                var w = new EventLogWatcher(q, null, true);
                w.EventRecordWritten += OnRecord;
                _tracker.MarkLive(DateTime.UtcNow);
                w.Enabled = true;
                _watcher = w;
                _tracker.EventsAvailable = true;
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _tracker.EventsAvailable = false;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher == null) return;
            try { _watcher.Enabled = false; } catch { }
            try { _watcher.Dispose(); } catch { }
            _watcher = null;
            _tracker.EventsAvailable = false;
        }
    }

    public void Dispose() => Stop();

    private void OnRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException != null)
        {
            Error = e.EventException.Message;
            return;
        }
        using var r = e.EventRecord;
        if (r == null) return;
        WlanEvent? ev;
        try { ev = WlanEvent.FromXml(r.ToXml(), r.RecordId, r.TimeCreated?.ToUniversalTime()); }
        catch { return; }
        if (ev == null || !_filter.Matches(ev)) return;
        _tracker.OnEvent(ev);
        try { EventReceived?.Invoke(ev); } catch { }
    }

    /// <summary>XPath for the events the tracker understands, optionally only since a time.</summary>
    public static string BuildXPath(DateTime? sinceUtc)
    {
        var ids = string.Join(" or ", WlanEvent.EventIds.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        var time = sinceUtc == null
            ? ""
            : " and TimeCreated[@SystemTime>='" + sinceUtc.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z']";
        return "*[System[(" + ids + ")" + time + "]]";
    }

    /// <summary>Reads the matching events of the last <paramref name="span"/> (oldest first). Never throws.</summary>
    public static List<WlanEvent> ReadRecent(TimeSpan span, out string? error)
    {
        var list = new List<WlanEvent>();
        error = null;
        try
        {
            var q = new EventLogQuery(LogName, PathType.LogName, BuildXPath(DateTime.UtcNow - span));
            using var reader = new EventLogReader(q);
            for (var r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
            {
                using (r)
                {
                    var ev = WlanEvent.FromXml(r.ToXml(), r.RecordId, r.TimeCreated?.ToUniversalTime());
                    if (ev != null) list.Add(ev);
                }
            }
        }
        catch (Exception ex) { error = ex.Message; }
        list.Sort((a, b) => a.TimeUtc != b.TimeUtc ? a.TimeUtc.CompareTo(b.TimeUtc) : (a.RecordId ?? 0).CompareTo(b.RecordId ?? 0));
        return list;
    }

    /// <summary>
    /// Replays recorded events through a fresh tracker (no live DHCP information, so "no IP" is not judged).
    /// Used by the diagnostics report.
    /// </summary>
    public static (List<JoinFinding> Findings, List<ClientJoinRecord> Devices) Replay(IEnumerable<WlanEvent> events, HotspotInterfaceFilter filter, DateTime nowUtc)
    {
        var t = new JoinTracker { EventsAvailable = true };
        t.MarkLive(nowUtc);
        var ctx = new JoinContext { JudgeNoIp = false };
        var findings = new List<JoinFinding>();
        DateTime? last = null;
        foreach (var e in events)
        {
            if (!filter.Matches(e)) continue;
            if (last != null)
            {
                // let the timeouts expire between events
                foreach (var step in new[] { 3, 9, 21 })
                {
                    var at = last.Value.AddSeconds(step);
                    if (at < e.TimeUtc) findings.AddRange(t.Tick(at, ctx));
                }
            }
            findings.AddRange(t.Tick(e.TimeUtc, ctx));
            t.OnEvent(e);
            last = e.TimeUtc;
        }
        if (last != null)
            foreach (var step in new[] { 3, 9, 21 })
            {
                var at = last.Value.AddSeconds(step);
                if (at < nowUtc) findings.AddRange(t.Tick(at, ctx));
            }
        findings.AddRange(t.Tick(nowUtc, ctx));
        return (findings, t.Snapshot());
    }

    /// <summary>Compact timeline of the most recent events of the hotspot interface (no passwords involved).</summary>
    public static List<string> Timeline(IEnumerable<WlanEvent> events, HotspotInterfaceFilter filter, int max)
    {
        var lines = new List<string>();
        foreach (var e in events.Where(filter.Matches).TakeLast(max))
        {
            var sb = new StringBuilder();
            sb.Append(e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ").Append(e.Id).Append(' ').Append(e.Kind);
            if (e.Kind == WlanEventKind.Associated && e.PeerMac.Length == 12) sb.Append("  peer=").Append(Util.MacUtil.Format(e.PeerMac));
            if (!string.IsNullOrEmpty(e.ConnectionId)) sb.Append("  conn=").Append(e.ConnectionId);
            if (e.ReasonCode is { } rc && rc != 0) sb.Append("  reason=").Append(rc);
            if (e.SecurityHintCode is { } sh && sh != 0) sb.Append("  hint=").Append(sh);
            lines.Add(sb.ToString());
        }
        return lines;
    }
}

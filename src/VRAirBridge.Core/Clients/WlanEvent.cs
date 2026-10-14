using System.Globalization;
using System.Xml.Linq;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Clients;

/// <summary>The Microsoft-Windows-WLAN-AutoConfig/Operational events the join monitor understands.</summary>
public enum WlanEventKind
{
    Other,
    /// <summary>20019: a peer associated with the hotspot (PeerMAC = the device's real MAC).</summary>
    Associated,
    /// <summary>11010: security (WPA2 4-way handshake) started. No peer MAC.</summary>
    SecurityStarted,
    /// <summary>20020: peer authenticated. PeerMAC here is a counter (01:00:01:00:00:00, 02:00:...), not a MAC.</summary>
    Authenticated,
    /// <summary>11005: security succeeded. No peer MAC.</summary>
    SecuritySucceeded,
    /// <summary>11004: security stopped (a peer left or its handshake failed). No peer MAC.</summary>
    SecurityStopped,
    /// <summary>8005: hotspot (GO) start begins.</summary>
    HotspotStarting,
    /// <summary>8006: hotspot start done.</summary>
    HotspotStarted,
    /// <summary>8000: the GO's own connection (WFD_GROUP_OWNER_PROFILE) starts.</summary>
    GoConnecting,
    /// <summary>8001: the GO's own connection is up.</summary>
    GoConnected,
    /// <summary>8008: hotspot stop begins.</summary>
    HotspotStopping,
    /// <summary>8003: the GO's own connection was disconnected.</summary>
    GoDisconnected,
    /// <summary>8010: hotspot stop failed.</summary>
    HotspotStopFailed,
}

/// <summary>One parsed WLAN AutoConfig event (only the fields the join monitor needs). Times are UTC; MACs are 12 upper-case hex digits.</summary>
public sealed class WlanEvent
{
    public const string GroupOwnerProfile = "WFD_GROUP_OWNER_PROFILE";

    /// <summary>Event IDs read from the log.</summary>
    public static readonly int[] EventIds = { 20019, 11010, 20020, 11005, 11004, 8005, 8006, 8000, 8001, 8008, 8003, 8010 };

    public long? RecordId { get; init; }
    public int Id { get; init; }
    public DateTime TimeUtc { get; init; }
    /// <summary>InterfaceGuid (20019/20020/80xx) or DeviceGuid (110xx).</summary>
    public string? InterfaceGuid { get; init; }
    /// <summary>InterfaceDescription (20019/20020/80xx) or Adapter (110xx).</summary>
    public string? Description { get; init; }
    /// <summary>LocalMAC / LocalMac, normalized.</summary>
    public string LocalMac { get; init; } = "";
    /// <summary>PeerMAC, normalized (only a real MAC for 20019).</summary>
    public string PeerMac { get; init; } = "";
    public string? ConnectionId { get; init; }
    public string? ProfileName { get; init; }
    public int? SecurityHintCode { get; init; }
    public int? ReasonCode { get; init; }

    public WlanEventKind Kind => KindOf(Id);

    public bool IsWifiDirect => Description != null && Description.StartsWith(NetworkInfo.WifiDirectPrefix, StringComparison.OrdinalIgnoreCase);

    public static WlanEventKind KindOf(int id) => id switch
    {
        20019 => WlanEventKind.Associated,
        11010 => WlanEventKind.SecurityStarted,
        20020 => WlanEventKind.Authenticated,
        11005 => WlanEventKind.SecuritySucceeded,
        11004 => WlanEventKind.SecurityStopped,
        8005 => WlanEventKind.HotspotStarting,
        8006 => WlanEventKind.HotspotStarted,
        8000 => WlanEventKind.GoConnecting,
        8001 => WlanEventKind.GoConnected,
        8008 => WlanEventKind.HotspotStopping,
        8003 => WlanEventKind.GoDisconnected,
        8010 => WlanEventKind.HotspotStopFailed,
        _ => WlanEventKind.Other,
    };

    /// <summary>Parses the XML of one event (EventRecord.ToXml()). Returns null when it is not an event of interest.</summary>
    public static WlanEvent? FromXml(string xml, long? recordId = null, DateTime? timeUtc = null)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch { return null; }
        var system = Child(root, "System");
        if (system == null) return null;
        if (!int.TryParse(Child(system, "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        if (KindOf(id) == WlanEventKind.Other) return null;

        var time = timeUtc;
        if (time == null)
        {
            var st = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (st != null && DateTime.TryParse(st, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                time = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }
        if (time == null) return null;
        if (recordId == null && long.TryParse(Child(system, "EventRecordID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid)) recordId = rid;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ed = Child(root, "EventData");
        if (ed != null)
        {
            foreach (var d in ed.Elements())
            {
                if (d.Name.LocalName != "Data") continue;
                var name = d.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name)) data[name] = d.Value;
            }
        }
        string? Get(params string[] names)
        {
            foreach (var n in names)
                if (data.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            return null;
        }
        static int? Int(string? s) =>
            s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        return new WlanEvent
        {
            RecordId = recordId,
            Id = id,
            TimeUtc = time.Value,
            InterfaceGuid = Get("InterfaceGuid", "DeviceGuid"),
            Description = Get("InterfaceDescription", "Adapter"),
            LocalMac = MacUtil.Normalize(Get("LocalMAC", "LocalMac")),
            PeerMac = MacUtil.Normalize(Get("PeerMAC", "PeerMac")),
            ConnectionId = Get("ConnectionId"),
            ProfileName = Get("ProfileName"),
            SecurityHintCode = Int(Get("SecurityHintCode")),
            ReasonCode = Int(Get("ReasonCode")),
        };
    }

    private static XElement? Child(XElement e, string localName) => e.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    public override string ToString() =>
        $"{TimeUtc:HH:mm:ss.fff} {Id} {Kind} guid={InterfaceGuid} local={LocalMac} peer={PeerMac} cid={ConnectionId}";
}

/// <summary>
/// Decides which WLAN events belong to the hotspot interface: the Wi-Fi Direct virtual adapter hosting the GO,
/// matched by its GUID or its MAC. Events of the internal Wi-Fi card and of the D-Link's own station connection
/// never match. GUIDs seen together with a known MAC are learned (the GUID can change when the hotspot restarts on
/// a new virtual adapter, the MAC stays the same).
/// </summary>
public sealed class HotspotInterfaceFilter
{
    private readonly HashSet<Guid> _guids = new();
    private readonly HashSet<string> _macs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Accept every Wi-Fi Direct virtual adapter (diagnostics while the hotspot is off).</summary>
    public bool AnyWifiDirect { get; init; }

    public bool HasInterface { get { lock (_lock) return _guids.Count > 0 || _macs.Count > 0; } }

    /// <summary>Adds the current GO adapter (NetworkInterface.Id GUID and MAC in any format).</summary>
    public void Add(string? guid, string? mac)
    {
        lock (_lock)
        {
            if (TryGuid(guid, out var g)) _guids.Add(g);
            var m = MacUtil.Normalize(mac);
            if (m.Length == 12) _macs.Add(m);
        }
    }

    public bool Matches(WlanEvent e)
    {
        lock (_lock)
        {
            bool hasGuid = TryGuid(e.InterfaceGuid, out var g);
            if (hasGuid && _guids.Contains(g)) return true;
            if (e.LocalMac.Length == 12 && _macs.Contains(e.LocalMac))
            {
                if (hasGuid) _guids.Add(g);
                return true;
            }
            if (AnyWifiDirect && e.IsWifiDirect)
            {
                if (hasGuid) _guids.Add(g);
                return true;
            }
            return false;
        }
    }

    private static bool TryGuid(string? s, out Guid g)
    {
        g = Guid.Empty;
        return !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s.Trim(), out g) && g != Guid.Empty;
    }
}

using System.Net;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.Clients;

/// <summary>Where a device is in joining the hotspot.</summary>
public enum JoinStage
{
    /// <summary>Associated (radio link up), password check not finished yet.</summary>
    Associated,
    /// <summary>Password check (WPA2 handshake) succeeded, no IP address seen yet.</summary>
    Authenticated,
    /// <summary>Has an IP address (DHCP lease from this app, or a neighbor entry on the hotspot adapter).</summary>
    GotIp,
    /// <summary>Disconnected (or the hotspot stopped).</summary>
    Left,
}

/// <summary>Why a join attempt failed.</summary>
public enum JoinFailure
{
    /// <summary>(a) Associated but never authenticated: the password check failed (WPA2 handshake not completed).</summary>
    WrongPassword,
    /// <summary>(b) Authenticated, then left within 5 s (this app's DHCP; 2 s otherwise) without any DHCP: the phone rejected the network right after the password step.</summary>
    DroppedAfterAuth,
    /// <summary>(c) Authenticated, but no DHCP request (our DHCP, 15 s) / no neighbor entry (ICS, 20 s): no IP address.</summary>
    NoIp,
    /// <summary>(d) At least 3 associate / authenticate / leave cycles within a few minutes without an IP.</summary>
    ReconnectLoop,
}

/// <summary>
/// One device as seen by the join monitor (one record per MAC). Times are UTC (DateTimeKind.Utc; call ToLocalTime() to show).
/// Mac is "AA-BB-CC-DD-EE-FF" like <see cref="ClientRow.Mac"/>. Failure is the latest failure; it is cleared when the device gets an IP.
/// </summary>
public sealed record ClientJoinRecord(
    string Mac,
    DateTime FirstSeen,
    DateTime LastChange,
    JoinStage Stage,
    string? Ip,
    string? HostName,
    JoinFailure? Failure,
    int Attempts)
{
    /// <summary>Locally administered (randomized "private Wi-Fi address", typical for iPhone / Android).</summary>
    public bool IsPrivateAddress
    {
        get
        {
            var m = MacUtil.Normalize(Mac);
            return m.Length == 12 && (Convert.ToByte(m[..2], 16) & 0x02) != 0;
        }
    }

    public bool IsConnected => Stage != JoinStage.Left;
}

/// <summary>
/// One result of the join monitor: a failure (a)–(d) or a successful join (Failure == null).
/// Already rate-limited: at most one per device and failure kind every 5 minutes.
/// </summary>
public sealed record JoinFinding(ClientJoinRecord Record, JoinFailure? Failure, DateTime TimeUtc)
{
    /// <summary>For <see cref="JoinFailure.NoIp"/>: true = this app was the DHCP server (no DHCP request arrived), false = Windows ICS.</summary>
    public bool OurDhcp { get; init; }
    /// <summary>Who handed out IPs when the finding was made.</summary>
    public JoinDhcp Dhcp { get; init; } = JoinDhcp.Other;
    /// <summary>For <see cref="JoinFailure.ReconnectLoop"/>: cycles counted in the window.</summary>
    public int Cycles { get; init; }
    /// <summary>For <see cref="JoinFailure.ReconnectLoop"/>: average seconds between cycles.</summary>
    public int IntervalSeconds { get; init; }
    /// <summary>True when the event happened before the monitor went live (read from the event log backlog).</summary>
    public bool FromBacklog { get; init; }
    /// <summary>For <see cref="JoinFailure.NoIp"/> with this app's DHCP: DHCP packets from the device did arrive (replies not accepted).</summary>
    public bool DhcpRequestsSeen { get; init; }
    /// <summary>The hotspot uses WPA3 (transition or only): some phones fail to join; WPA2 is the safe choice.</summary>
    public bool Wpa3 { get; init; }
    /// <summary>Seconds from association to this result (success: until the IP; failures: until the device left / timed out).</summary>
    public int ElapsedSeconds { get; init; }
    /// <summary>When the join attempt started (association), UTC.</summary>
    public DateTime AttemptUtc { get; init; }

    public bool IsSuccess => Failure == null;

    /// <summary>The log line / toast text in the current UI language.</summary>
    public string Message => JoinText.Describe(this);
}

/// <summary>How connected devices get their IP address right now.</summary>
public enum JoinDhcp
{
    /// <summary>This app's DHCP server: DHCP packets and leases are visible.</summary>
    Ours,
    /// <summary>Windows Internet Connection Sharing hands out the IPs: only the neighbor table is visible.</summary>
    Ics,
    /// <summary>Nobody / unknown (DHCP off, waiting or failed).</summary>
    Other,
}

/// <summary>A DHCP lease as the join tracker needs it.</summary>
public sealed record JoinLease(string Ip, string? HostName, DateTime LastSeenUtc, bool Active);

/// <summary>
/// Snapshot of the IP-address evidence for one watchdog tick: DHCP leases / packets of this app's DHCP server,
/// the neighbor table of the hotspot adapter and the devices Windows lists as connected. All keys are normalized MACs.
/// </summary>
public sealed class JoinContext
{
    public JoinDhcp Dhcp { get; init; } = JoinDhcp.Other;

    /// <summary>The hotspot is set to WPA3 (transition or only).</summary>
    public bool Wpa3 { get; init; }

    /// <summary>False = do not judge "no IP" (c) at all (diagnostics replay without live DHCP information).</summary>
    public bool JudgeNoIp { get; init; } = true;

    public Dictionary<string, JoinLease> Leases { get; } = new(StringComparer.Ordinal);

    /// <summary>Last DHCP packet (any type) this app's DHCP server received from the MAC.</summary>
    public Dictionary<string, DateTime> DhcpPackets { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, NeighborEntry> Neighbors { get; } = new(StringComparer.Ordinal);

    /// <summary>MACs Windows reports as connected right now; null = unknown.</summary>
    public HashSet<string>? Present { get; set; }

    /// <summary>Host names Windows reports for connected devices.</summary>
    public Dictionary<string, string> HostNames { get; } = new(StringComparer.Ordinal);

    public JoinContext AddLease(string mac, string ip, string? hostName, DateTime lastSeenUtc, bool active)
    {
        var m = MacUtil.Normalize(mac);
        if (m.Length == 12 && !string.IsNullOrWhiteSpace(ip)) Leases[m] = new JoinLease(ip, string.IsNullOrWhiteSpace(hostName) ? null : hostName, lastSeenUtc, active);
        return this;
    }

    public JoinContext AddDhcpPacket(string mac, DateTime lastPacketUtc)
    {
        var m = MacUtil.Normalize(mac);
        if (m.Length == 12 && lastPacketUtc > DateTime.MinValue)
            DhcpPackets[m] = DhcpPackets.TryGetValue(m, out var t) && t > lastPacketUtc ? t : lastPacketUtc;
        return this;
    }

    public JoinContext AddNeighbor(NeighborEntry n)
    {
        var m = MacUtil.Normalize(n.Mac);
        if (m.Length != 12) return this;
        // prefer the freshest entry when a MAC has several addresses
        if (!Neighbors.TryGetValue(m, out var cur) || Rank(n) > Rank(cur)) Neighbors[m] = n;
        return this;
    }

    public JoinContext AddPresent(string mac, string? hostName = null)
    {
        var m = MacUtil.Normalize(mac);
        if (m.Length != 12) return this;
        Present ??= new HashSet<string>(StringComparer.Ordinal);
        Present.Add(m);
        if (!string.IsNullOrWhiteSpace(hostName) && !IPAddress.TryParse(hostName, out _)) HostNames[m] = hostName;
        return this;
    }

    private static int Rank(NeighborEntry n) => NetworkInfo.IsApipa(n.Address) ? 0 : n.IsFresh ? 3 : n.State == NeighborState.Stale ? 2 : 1;
}

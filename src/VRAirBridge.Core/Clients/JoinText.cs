using System.Globalization;

namespace VRAirBridge.Core.Clients;

/// <summary>User-facing texts of the join diagnostics (current UI language).</summary>
public static class JoinText
{
    /// <summary>"Alans-iPhone (AA-BB-…)" or "Device AA-BB-… (private address)".</summary>
    public static string Name(ClientJoinRecord r) =>
        !string.IsNullOrWhiteSpace(r.HostName)
            ? Loc.T("join.nameWithMac", r.HostName, r.Mac)
            : Loc.T(r.IsPrivateAddress ? "join.devicePrivate" : "join.device", r.Mac);

    /// <summary>The log line / notification text for one finding, with what to do next.</summary>
    public static string Describe(JoinFinding f)
    {
        var name = Name(f.Record);
        var text = f.Failure switch
        {
            null => Loc.T("join.ok", name, f.Record.Ip ?? "?", f.ElapsedSeconds),
            JoinFailure.WrongPassword => Loc.T("join.wrongPassword", name),
            JoinFailure.DroppedAfterAuth => f.Wpa3
                ? Loc.T("join.withHint", Loc.T("join.hintWpa3"), Loc.T("join.dropped", name, f.ElapsedSeconds))
                : Loc.T("join.dropped", name, f.ElapsedSeconds),
            JoinFailure.NoIp => Loc.T("join.withHint", NoIpText(f, name), NoIpHint(f)),
            JoinFailure.ReconnectLoop => Loc.T("join.withHint", Loc.T("join.loop", name, f.Cycles, f.IntervalSeconds), NoIpHint(f)),
            _ => name,
        };
        if (f.FromBacklog)
            text = Loc.T("join.earlier", f.AttemptUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)) + text;
        return text;
    }

    /// <summary>Notification title for a failure.</summary>
    public static string Title(JoinFinding f) =>
        f.Failure == JoinFailure.WrongPassword ? Loc.T("join.titleWrongPassword") : Loc.T("join.titleFailed");

    /// <summary>Short stage text for the device list.</summary>
    public static string Stage(JoinStage s) => s switch
    {
        JoinStage.Associated => Loc.T("join.stage.associated"),
        JoinStage.Authenticated => Loc.T("join.stage.authenticated"),
        JoinStage.GotIp => Loc.T("join.stage.gotIp"),
        _ => Loc.T("join.stage.left"),
    };

    /// <summary>Short failure text for the device list.</summary>
    public static string Failure(JoinFailure f) => f switch
    {
        JoinFailure.WrongPassword => Loc.T("join.failure.wrongPassword"),
        JoinFailure.DroppedAfterAuth => Loc.T("join.failure.droppedAfterAuth"),
        JoinFailure.NoIp => Loc.T("join.failure.noIp"),
        _ => Loc.T("join.failure.reconnectLoop"),
    };

    /// <summary>What to do about a failure, for the device list tooltip / detail line.</summary>
    public static string Hint(JoinFailure f, JoinDhcp dhcp, bool dhcpRequestsSeen, bool wpa3) => f switch
    {
        JoinFailure.WrongPassword => Loc.T("join.hintWrongPassword"),
        JoinFailure.DroppedAfterAuth => wpa3 ? Loc.T("join.hintWpa3") : Loc.T("join.hintDropped"),
        _ => HintFor(dhcp, dhcpRequestsSeen),
    };

    private static string NoIpText(JoinFinding f, string name) => f.Dhcp switch
    {
        JoinDhcp.Ours => Loc.T(f.DhcpRequestsSeen ? "join.noIpBlocked" : "join.noIpOurs", name, f.ElapsedSeconds),
        JoinDhcp.Ics => Loc.T("join.noIpIcs", name, f.ElapsedSeconds),
        _ => Loc.T("join.noIpNone", name, f.ElapsedSeconds),
    };

    private static string NoIpHint(JoinFinding f) => HintFor(f.Dhcp, f.DhcpRequestsSeen);

    private static string HintFor(JoinDhcp dhcp, bool dhcpRequestsSeen) => dhcp switch
    {
        JoinDhcp.Ours => Loc.T(dhcpRequestsSeen ? "join.hintFirewall" : "join.hintRejoin"),
        JoinDhcp.Ics => Loc.T("join.hintIcs"),
        _ => Loc.T("join.hintNone"),
    };
}

using VRAirBridge.Core;
using VRAirBridge.Core.Clients;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Util;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>Device list: join attempts seen in the WLAN event log (password check, failures) next to the connected devices.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Failed join attempts stay in the list this long after the last event.</summary>
    private static readonly TimeSpan FailedJoinShown = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Adds the join state to the rows: a device still joining shows its stage, a device whose last attempt failed shows the
    /// failure and what to do (red). Failed attempts of devices Windows does not list (they never got in) become rows of their own.
    /// </summary>
    internal void MergeJoins(List<DeviceRowVm> list, IReadOnlyList<ClientJoinRecord> joins, DhcpProvider dhcp, DateTime nowUtc)
    {
        if (joins.Count == 0) return;
        var joinDhcp = dhcp switch { DhcpProvider.Ours => JoinDhcp.Ours, DhcpProvider.Ics => JoinDhcp.Ics, _ => JoinDhcp.Other };
        bool wpa3 = _s.Auth.Contains("WPA3", StringComparison.OrdinalIgnoreCase);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++) index.TryAdd(MacUtil.Normalize(list[i].Mac), i);

        foreach (var j in joins)
        {
            var key = MacUtil.Normalize(j.Mac);
            bool failed = j.Failure != null && j.Ip == null && j.Stage != JoinStage.GotIp;
            bool joining = j.Failure == null && j.Stage is JoinStage.Associated or JoinStage.Authenticated;
            if (index.TryGetValue(key, out var i))
            {
                var row = list[i];
                bool noIp = row.Ip.Length == 0 || row.Ip == "\u2014";
                if (failed && noIp)
                    list[i] = Copy(row, JoinText.Failure(j.Failure!.Value), Join(row.Note, PrivateNote(j)), FailureHint(j, joinDhcp, wpa3), Tone.Bad);
                else if (joining && noIp && row.Online)
                    list[i] = Copy(row, JoinText.Stage(j.Stage), row.Note, row.Hint, Tone.Warn);
                continue;
            }
            if (!failed && !joining) continue;
            if (failed && nowUtc - j.LastChange > FailedJoinShown) continue;
            index[key] = list.Count;
            list.Add(new DeviceRowVm
            {
                Name = string.IsNullOrWhiteSpace(j.HostName) ? Loc.T("ui.devices.unknown") : j.HostName!,
                Mac = j.Mac,
                Ip = "\u2014",
                State = failed ? JoinText.Failure(j.Failure!.Value) : JoinText.Stage(j.Stage),
                LastSeen = j.LastChange.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                Note = PrivateNote(j),
                Hint = failed ? FailureHint(j, joinDhcp, wpa3) : "",
                Online = joining,
                Tone = failed ? Tone.Bad : Tone.Warn,
            });
        }
    }

    private static string PrivateNote(ClientJoinRecord j) => j.IsPrivateAddress ? Loc.T("ui.devices.privateMac") : "";

    private static string FailureHint(ClientJoinRecord j, JoinDhcp dhcp, bool wpa3) => JoinText.Hint(j.Failure!.Value, dhcp, false, wpa3);

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 || a.Contains(b, StringComparison.Ordinal) ? a : a + Loc.T("common.listSep") + b;

    private static DeviceRowVm Copy(DeviceRowVm r, string state, string note, string hint, Tone tone) => new()
    {
        Name = r.Name,
        Mac = r.Mac,
        Ip = r.Ip,
        State = state,
        LastSeen = r.LastSeen,
        Note = note,
        Hint = hint,
        Online = r.Online,
        Tone = tone,
    };
}

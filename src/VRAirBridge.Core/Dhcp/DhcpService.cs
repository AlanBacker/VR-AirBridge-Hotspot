using System.Net;
using System.Net.Sockets;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.Core.Dhcp;

public enum DhcpProvider { None, Ours, Ics, Waiting, Error, Disabled }

public sealed record DhcpStatus(DhcpProvider Provider, string Text, string? Detail)
{
    public bool Serving => Provider is DhcpProvider.Ours or DhcpProvider.Ics;
}

/// <summary>
/// 决定由谁给热点上的设备发 IP：ICS 的 DHCP 正常绑定在热点网卡上就交给 ICS；
/// 否则（被 Hyper-V Default Switch 抢走、ICS 幽灵记录等）由本软件的内置 DHCP 服务器提供。
/// 每个看门狗周期调用 <see cref="Reconcile"/>，随时根据实际情况切换。
/// </summary>
public sealed class DhcpService : IDisposable
{
    /// <summary>热点网卡拿到 IP 后先等 ICS 这么久，给它机会先绑定 67 端口。</summary>
    public static readonly TimeSpan IcsGrace = TimeSpan.FromSeconds(7);

    private readonly Func<AppSettings> _settings;
    private readonly DhcpLeaseStore _leases;
    private DhcpEngine? _engine;
    private DhcpServer? _server;
    private string? _serverKey;
    private DateTime? _goIpSince;
    private string? _goIpKey;
    private int _icsSeenCount;
    private DateTime _nextStartAttempt = DateTime.MinValue;
    private string? _lastStartError;
    private DhcpStatus _status = new(DhcpProvider.None, Loc.T("dhcp.notRunning"), null);
    private string? _lastLogged;

    public event Action<LeaseEvent>? LeaseChanged;

    public DhcpService(Func<AppSettings> settings, string? leasePath)
    {
        _settings = settings;
        _leases = new DhcpLeaseStore(leasePath);
        _leases.Prune(DateTime.UtcNow, TimeSpan.FromDays(7));
    }

    public DhcpStatus Status => _status;
    public DhcpServer? Server => _server;
    public DhcpEngine? Engine => _engine;
    public DhcpLeaseStore Leases => _leases;
    public bool OursRunning => _server?.IsRunning == true;

    /// <summary>其它进程（通常是 ICS 的 svchost）在 ip:port 上监听 UDP。</summary>
    public static List<UdpListener> ForeignListeners(int port)
    {
        int self = Environment.ProcessId;
        return IpHelper.GetUdpListeners().Where(l => l.Port == port && l.Pid != self).ToList();
    }

    /// <summary>根据当前情况选择下发给设备的 DNS。</summary>
    public static List<IPAddress> ChooseDns(GoAdapterInfo go, UplinkInfo? uplink, IEnumerable<string> fallback)
    {
        var res = new List<IPAddress>();
        // ICS 的 DNS 代理在热点 IP:53 上 → 直接让设备用电脑当 DNS
        if (go.Ip != null && ForeignListeners(53).Any(l => l.Address.Equals(go.Ip)))
        {
            res.Add(go.Ip);
            return res;
        }
        if (uplink != null)
        {
            foreach (var d in uplink.Dns)
            {
                if (d.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(d) || NetworkInfo.IsApipa(d) || d.Equals(IPAddress.Any)) continue;
                if (go.Mask != null && go.Ip != null && NetworkInfo.SameSubnet(d, go.Ip, go.Mask)) continue;
                if (!res.Contains(d)) res.Add(d);
            }
        }
        foreach (var f in fallback)
            if (IPAddress.TryParse(f, out var a) && a.AddressFamily == AddressFamily.InterNetwork && !res.Contains(a)) res.Add(a);
        if (res.Count == 0) res.Add(IPAddress.Parse("223.5.5.5"));
        return res.Take(4).ToList();
    }

    /// <summary>
    /// 每个周期调用。hotspotOn = 热点开着；canServe = 允许启动内置服务器（需要管理员，因为要放行防火墙）。
    /// </summary>
    public DhcpStatus Reconcile(GoAdapterInfo? go, UplinkInfo? uplink, bool hotspotOn, bool canServe)
    {
        var s = _settings();
        var mode = (s.Dhcp.Mode ?? "auto").Trim().ToLowerInvariant();

        if (!hotspotOn || go == null || !go.HasUsableIp || go.Ip == null || go.Mask == null)
        {
            StopServer(hotspotOn ? Loc.T("dhcp.hotspotAdapterHasNoIp") : Loc.T("dhcp.hotspotOff"));
            _goIpSince = null;
            _goIpKey = null;
            _icsSeenCount = 0;
            return Set(new DhcpStatus(DhcpProvider.None, hotspotOn ? Loc.T("dhcp.notRunningWaitingHotspotAdapter") : Loc.T("dhcp.notRunning"), null));
        }

        var key = $"{go.Ip}/{go.Mask}/{go.IfIndex}";
        if (_goIpKey != key) { _goIpKey = key; _goIpSince = DateTime.UtcNow; _icsSeenCount = 0; }

        var listeners67 = ForeignListeners(67);
        bool icsOnGo = listeners67.Any(l => l.Address.Equals(go.Ip));
        _icsSeenCount = icsOnGo ? _icsSeenCount + 1 : 0;
        var elsewhere = listeners67.Where(l => !l.Address.Equals(go.Ip)).Select(l => l.Address.ToString()).Distinct().ToList();
        string? elsewhereText = elsewhere.Count == 0 ? null
            : Loc.T("dhcp.icsBoundElsewhere", string.Join(Loc.T("common.enumSep"), elsewhere.Select(a => a + DescribeAddress(a))));

        if (mode == "off")
        {
            StopServer(Loc.T("dhcp.setNotProvideDhcp"));
            return Set(icsOnGo
                ? new DhcpStatus(DhcpProvider.Ics, Loc.T("dhcp.providedByWindowsIcs"), null)
                : new DhcpStatus(DhcpProvider.Disabled, Loc.T("dhcp.notRunningDhcpModeOff"), elsewhereText));
        }

        if (mode != "always")
        {
            if (OursRunning)
            {
                // ICS 连续两次被看到绑在热点 IP 上，说明它恢复了 → 让给它
                if (_icsSeenCount >= 2)
                {
                    StopServer(Loc.T("dhcp.dhcpWindowsIcsWorkingHotspot"));
                    return Set(new DhcpStatus(DhcpProvider.Ics, Loc.T("dhcp.providedByWindowsIcs"), null));
                }
            }
            else
            {
                if (icsOnGo) return Set(new DhcpStatus(DhcpProvider.Ics, Loc.T("dhcp.providedByWindowsIcs"), null));
                if (_goIpSince != null && DateTime.UtcNow - _goIpSince < IcsGrace)
                    return Set(new DhcpStatus(DhcpProvider.Waiting, Loc.T("dhcp.waitingWindowsIcs"), elsewhereText));
            }
        }
        else if (icsOnGo && !OursRunning)
        {
            Logger.Warn(Loc.T("dhcp.dhcpModeAlwaysDhcpWindows"));
        }

        var dns = ChooseDns(go, uplink, s.Dhcp.FallbackDns);
        var cfg = DhcpEngineConfig.Create(go.Ip, go.Mask, s.Dhcp, dns, s.Internet.Allow);

        if (OursRunning)
        {
            if (_serverKey != key)
            {
                StopServer(Loc.T("dhcp.hotspotAdapterAddressChangedRestarting"));
            }
            else
            {
                var old = _engine!.Config;
                if (!old.Dns.SequenceEqual(cfg.Dns) || !old.PoolStart.Equals(cfg.PoolStart) || !old.PoolEnd.Equals(cfg.PoolEnd) ||
                    old.LeaseTime != cfg.LeaseTime || old.DomainName != cfg.DomainName || old.AllowInternet != cfg.AllowInternet)
                {
                    _engine.UpdateConfig(cfg);
                    Logger.Info(Loc.T("dhcp.dhcpSettingsUpdatedPool", cfg.PoolStart, cfg.PoolEnd,
                                cfg.AllowInternet ? $"DNS {string.Join(", ", cfg.Dns)}" : Loc.T("dhcp.lanOnlyNoDnsHanded")));
                }
                return Set(OursStatus(elsewhereText));
            }
        }

        if (!canServe)
        {
            return Set(new DhcpStatus(DhcpProvider.Error,
                Loc.T("dhcp.notRunningIcsDhcpIsn"), elsewhereText));
        }

        if (DateTime.UtcNow < _nextStartAttempt)
            return Set(new DhcpStatus(DhcpProvider.Error, Loc.T("dhcp.failedStartWillRetry") + _lastStartError, elsewhereText));

        try
        {
            if (elsewhereText != null) Logger.Warn(elsewhereText + Loc.T("dhcp.appTakesOverHandingOut"));
            else Logger.Info(Loc.T("dhcp.noDhcpServiceHotspotAdapter"));
            _engine = new DhcpEngine(cfg, _leases);
            _engine.LeaseChanged += e => { try { LeaseChanged?.Invoke(e); } catch { } };
            _server = new DhcpServer(_engine, new DhcpServerOptions
            {
                ServerIp = go.Ip,
                InterfaceIndex = go.IfIndex,
                BindAny = true,
            });
            _server.Start();
            _serverKey = key;
            _lastStartError = null;
            return Set(OursStatus(elsewhereText));
        }
        catch (Exception ex)
        {
            _server?.Dispose();
            _server = null;
            _engine = null;
            _lastStartError = ex.Message;
            _nextStartAttempt = DateTime.UtcNow.AddSeconds(15);
            Logger.Error(Loc.T("dhcp.builtDhcpServerFailedStart") + ex.Message);
            return Set(new DhcpStatus(DhcpProvider.Error, Loc.T("dhcp.failedStart") + ex.Message, elsewhereText));
        }
    }

    private DhcpStatus OursStatus(string? why)
    {
        var srv = _server!;
        var detail = Loc.T("dhcp.listeningReceivedPacketsSentReplies", srv.BindText, srv.Received, srv.Sent);
        if (why != null) detail = why + Loc.T("common.period") + detail;
        var lanOnly = _engine != null && !_engine.Config.AllowInternet;
        return new DhcpStatus(DhcpProvider.Ours, lanOnly ? Loc.T("dhcp.providedByApp", InternetStrings.DhcpLanOnlyNote) : Loc.T("dhcp.providedByApp2"), detail);
    }

    private static string DescribeAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return "";
        if (a.Equals(IPAddress.Any)) return Loc.T("dhcp.allAdapters");
        var name = NetworkInfo.AdapterNameForIp(a);
        return name == null ? "" : Loc.T("common.bracket", name);
    }

    private DhcpStatus Set(DhcpStatus st)
    {
        _status = st;
        var k = st.Provider + "|" + st.Text;
        if (k != _lastLogged)
        {
            _lastLogged = k;
            Logger.Info(Loc.T("dhcp.dhcpStatus") + st.Text + (st.Provider == DhcpProvider.Ours || st.Detail == null ? "" : Loc.T("common.paren", st.Detail)));
        }
        return st;
    }

    public void StopServer(string reason)
    {
        if (_server == null) return;
        Logger.Info(Loc.T("dhcp.stoppingBuiltDhcpServer") + reason);
        try { _server.Stop(); } catch { }
        _server = null;
        _serverKey = null;
        _leases.Save();
    }

    public List<string> StuckClients() => _engine?.GetStuckClients() ?? new List<string>();

    public Dictionary<string, DhcpEngine.ClientTrace> Traces() =>
        _engine?.GetTraces() ?? new Dictionary<string, DhcpEngine.ClientTrace>(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => StopServer(Loc.T("dhcp.appExiting"));
}

using System.Net;
using System.Text;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.Core.Dhcp;

/// <summary>
/// DHCP 服务的参数（服务器地址、地址池、租期、下发的网关/DNS）。
/// AllowInternet = false（仅局域网）时不下发 DNS（选项 6）和域名（选项 15），其余照常（地址、掩码、租期、服务器标识、网关 = 本机）。
/// </summary>
public sealed record DhcpEngineConfig(
    IPAddress ServerIp,
    IPAddress SubnetMask,
    IPAddress PoolStart,
    IPAddress PoolEnd,
    TimeSpan LeaseTime,
    IPAddress Router,
    IReadOnlyList<IPAddress> Dns,
    string? DomainName,
    bool AllowInternet = true)
{
    /// <summary>实际会下发给设备的 DNS（仅局域网时为空）。</summary>
    public IReadOnlyList<IPAddress> AdvertisedDns => AllowInternet ? Dns : Array.Empty<IPAddress>();

    public IPAddress BroadcastAddress
    {
        get
        {
            uint ip = NetworkInfo.ToUInt32(ServerIp), m = NetworkInfo.ToUInt32(SubnetMask);
            return NetworkInfo.FromUInt32((ip & m) | ~m);
        }
    }

    public int PoolSize => (int)(NetworkInfo.ToUInt32(PoolEnd) - NetworkInfo.ToUInt32(PoolStart) + 1);

    /// <summary>
    /// 根据热点网卡的实际 IP/掩码和用户设置生成配置。设置里的地址池如果不在热点网段内，
    /// 就保留主机号部分、换到热点网段（例如 .100–.199）。
    /// </summary>
    public static DhcpEngineConfig Create(IPAddress serverIp, IPAddress mask, DhcpSettings s, IReadOnlyList<IPAddress> dns, bool allowInternet = true)
    {
        uint ip = NetworkInfo.ToUInt32(serverIp), m = NetworkInfo.ToUInt32(mask);
        uint net = ip & m, bcast = net | ~m;
        uint start = ParseOr(s.PoolStart, net | 100), end = ParseOr(s.PoolEnd, net | 199);
        if ((start & m) != net) start = net | (start & ~m);
        if ((end & m) != net) end = net | (end & ~m);
        if (start <= net) start = net + 1;
        if (end >= bcast) end = bcast - 1;
        if (end < start) { start = net + 1; end = bcast - 1; }
        var hours = s.LeaseHours <= 0 ? 12 : Math.Clamp(s.LeaseHours, 0.05, 24 * 30);
        return new DhcpEngineConfig(serverIp, mask, NetworkInfo.FromUInt32(start), NetworkInfo.FromUInt32(end),
            TimeSpan.FromHours(hours), serverIp, dns, string.IsNullOrWhiteSpace(s.DomainName) ? null : s.DomainName.Trim(), allowInternet);
    }

    private static uint ParseOr(string? s, uint fallback) =>
        IPAddress.TryParse(s, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? NetworkInfo.ToUInt32(a) : fallback;
}

/// <summary>引擎给出的应答：报文 + 应该发往的地址。</summary>
public sealed record DhcpReply(DhcpPacket Packet, IPAddress Destination, string Description);

/// <summary>租约变化事件的类型。</summary>
public enum LeaseEventKind { Granted, Renewed, Released, Declined }

public sealed record LeaseEvent(LeaseEventKind Kind, DhcpLease Lease);

/// <summary>
/// DHCP 协议逻辑（RFC 2131），不涉及网络收发，方便单元测试。
/// 本服务器是“权威”服务器：客户端在本网段请求不属于它的地址时回 NAK，让它重新 DISCOVER。
/// 租约文件丢失时，客户端 INIT-REBOOT 请求的地址只要空闲就直接确认（宽松模式），避免设备断流。
/// </summary>
public sealed class DhcpEngine
{
    private readonly object _lock = new();
    private readonly DhcpLeaseStore _store;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, (IPAddress Ip, DateTime Until)> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, DateTime> _declined = new();
    private readonly Dictionary<string, ClientTrace> _trace = new(StringComparer.OrdinalIgnoreCase);
    private DhcpEngineConfig _cfg;

    public static readonly TimeSpan OfferHold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DeclineBlock = TimeSpan.FromMinutes(10);

    /// <summary>租约变化（在线程池线程上触发）。</summary>
    public event Action<LeaseEvent>? LeaseChanged;

    /// <summary>为 true 时租约变化同步保存并同步触发事件（单元测试用）。</summary>
    public bool Synchronous { get; set; }

    public DhcpEngine(DhcpEngineConfig cfg, DhcpLeaseStore store, Func<DateTime>? utcClock = null)
    {
        _cfg = cfg;
        _store = store;
        _clock = utcClock ?? (() => DateTime.UtcNow);
    }

    public DhcpEngineConfig Config { get { lock (_lock) return _cfg; } }

    public void UpdateConfig(DhcpEngineConfig cfg)
    {
        lock (_lock) _cfg = cfg;
    }

    public DhcpLeaseStore Store => _store;

    /// <summary>每个客户端的收发统计，用于判断“发了 OFFER 但客户端收不到”（防火墙/杀毒拦截）。</summary>
    public sealed class ClientTrace
    {
        public DateTime FirstDiscover;
        public DateTime LastDiscover;
        public DateTime LastPacket;
        public int Discovers;
        public int Offers;
        public int Requests;
        public int Acks;
        public int Naks;
        public DateTime? LastAck;

        public ClientTrace Copy() => (ClientTrace)MemberwiseClone();
    }

    public Dictionary<string, ClientTrace> GetTraces()
    {
        lock (_lock) return _trace.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 最近 2 分钟内反复 DISCOVER、我们也回了 OFFER，但从没收到它的 REQUEST 的客户端 MAC。
    /// 这种情况几乎都是 OFFER 没送到客户端（防火墙 / 卡巴斯基等安全软件拦截了本软件发出的 UDP 67→68）。
    /// </summary>
    public List<string> GetStuckClients()
    {
        var now = _clock();
        lock (_lock)
        {
            return _trace.Where(kv => kv.Value.Offers >= 3 && kv.Value.Requests == 0 && now - kv.Value.LastDiscover < TimeSpan.FromMinutes(2))
                         .Select(kv => kv.Key).ToList();
        }
    }

    private ClientTrace Trace(string mac)
    {
        if (!_trace.TryGetValue(mac, out var t)) _trace[mac] = t = new ClientTrace();
        t.LastPacket = _clock();
        return t;
    }

    /// <summary>处理一个收到的报文；返回 null 表示不需要应答。log 输出一行便于调试的说明。</summary>
    public DhcpReply? Handle(DhcpPacket req, out string log)
    {
        lock (_lock)
        {
            var type = req.MessageType;
            var mac = req.ClientMac;
            if (req.Op != 1) { log = Loc.T("dhcpEngine.ignoredNotClientRequestOp", req.Op); return null; }
            if (req.HType != 1 || req.HLen != 6) { log = Loc.T("dhcpEngine.ignoredNotEthernetHardwareAddress", req.HType, req.HLen); return null; }
            if (type == null) { log = Loc.T("dhcpEngine.ignoredBootpPacketNoOption"); return null; }
            if (!req.GiAddr.Equals(IPAddress.Any)) { log = Loc.T("dhcpEngine.ignoredRelayedPacketGiaddr", req.GiAddr); return null; }
            CleanupExpired();
            return type switch
            {
                DhcpMessageType.Discover => OnDiscover(req, mac, out log),
                DhcpMessageType.Request => OnRequest(req, mac, out log),
                DhcpMessageType.Decline => OnDecline(req, mac, out log),
                DhcpMessageType.Release => OnRelease(req, mac, out log),
                DhcpMessageType.Inform => OnInform(req, mac, out log),
                _ => Ignore(type.Value, out log),
            };
        }
    }

    private static DhcpReply? Ignore(DhcpMessageType t, out string log)
    {
        log = Loc.T("dhcpEngine.ignoredServerTypePacket") + DhcpPacket.TypeName(t);
        return null;
    }

    private void CleanupExpired()
    {
        var now = _clock();
        foreach (var k in _offers.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList()) _offers.Remove(k);
        foreach (var k in _declined.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _declined.Remove(k);
        foreach (var k in _trace.Where(kv => now - kv.Value.LastPacket > TimeSpan.FromMinutes(30)).Select(kv => kv.Key).ToList()) _trace.Remove(k);
    }

    private void Commit(Action? afterSave = null)
    {
        if (Synchronous) { _store.Save(); afterSave?.Invoke(); return; }
        ThreadPool.QueueUserWorkItem(_ => { _store.Save(); try { afterSave?.Invoke(); } catch { } });
    }

    private void Raise(LeaseEvent ev)
    {
        try { LeaseChanged?.Invoke(ev); } catch { }
    }

    // ---------------------------------------------------------------- 地址池

    public bool InPool(IPAddress ip)
    {
        uint v = NetworkInfo.ToUInt32(ip);
        return v >= NetworkInfo.ToUInt32(_cfg.PoolStart) && v <= NetworkInfo.ToUInt32(_cfg.PoolEnd) && !ip.Equals(_cfg.ServerIp);
    }

    private bool OnSubnet(IPAddress ip) => NetworkInfo.SameSubnet(ip, _cfg.ServerIp, _cfg.SubnetMask);

    /// <summary>这个地址能否给这个 MAC：在池内、没被别人占用/预留、没被拒绝。</summary>
    private bool AvailableFor(IPAddress ip, string mac)
    {
        if (!InPool(ip)) return false;
        var now = _clock();
        if (_declined.ContainsKey(NetworkInfo.ToUInt32(ip))) return false;
        var holder = _store.GetByIp(ip.ToString());
        if (holder != null && !string.Equals(holder.Mac, mac, StringComparison.OrdinalIgnoreCase) && holder.IsActive(now)) return false;
        foreach (var kv in _offers)
            if (!string.Equals(kv.Key, mac, StringComparison.OrdinalIgnoreCase) && kv.Value.Ip.Equals(ip)) return false;
        return true;
    }

    private IPAddress? Allocate(string mac, IPAddress? requested)
    {
        var now = _clock();
        // 1) 之前给过这个 MAC 的地址（包括已过期的），保证同一设备总拿到同一个 IP
        if (_offers.TryGetValue(mac, out var off) && AvailableFor(off.Ip, mac)) return off.Ip;
        var old = _store.GetByMac(mac);
        if (old != null && IPAddress.TryParse(old.Ip, out var oldIp) && AvailableFor(oldIp, mac)) return oldIp;
        // 2) 客户端自己想要的地址
        if (requested != null && AvailableFor(requested, mac)) return requested;
        // 3) 池里从没用过的地址
        var all = _store.All();
        var used = new HashSet<string>(all.Select(l => l.Ip));
        uint s = NetworkInfo.ToUInt32(_cfg.PoolStart), e = NetworkInfo.ToUInt32(_cfg.PoolEnd);
        for (uint v = s; v <= e && v >= s; v++)
        {
            var ip = NetworkInfo.FromUInt32(v);
            if (!used.Contains(ip.ToString()) && AvailableFor(ip, mac)) return ip;
        }
        // 4) 回收过期最久的地址
        foreach (var l in all.Where(l => !l.IsActive(now)).OrderBy(l => l.Expires))
            if (IPAddress.TryParse(l.Ip, out var ip) && AvailableFor(ip, mac)) return ip;
        return null;
    }

    // ---------------------------------------------------------------- 报文处理

    private DhcpReply? OnDiscover(DhcpPacket req, string mac, out string log)
    {
        var now = _clock();
        var t = Trace(mac);
        if (t.Discovers == 0 || now - t.LastDiscover > TimeSpan.FromMinutes(2)) { t.FirstDiscover = now; t.Offers = 0; t.Requests = 0; }
        t.Discovers++;
        t.LastDiscover = now;

        var requested = req.GetIpOption(DhcpOption.RequestedIp);
        var ip = Allocate(mac, requested);
        if (ip == null)
        {
            log = Loc.T("dhcpEngine.discoverFromAddressPoolFull", mac, _cfg.PoolStart, _cfg.PoolEnd);
            return null;
        }
        _offers[mac] = (ip, now + OfferHold);
        t.Offers++;
        var reply = BuildReply(req, DhcpMessageType.Offer, ip);
        log = Loc.T("dhcpEngine.discoverFromOffer", mac, HostSuffix(req), ip);
        return new DhcpReply(reply, Destination(req), "OFFER " + ip);
    }

    private DhcpReply? OnRequest(DhcpPacket req, string mac, out string log)
    {
        var now = _clock();
        var t = Trace(mac);
        t.Requests++;
        var serverId = req.GetIpOption(DhcpOption.ServerId);
        var requested = req.GetIpOption(DhcpOption.RequestedIp);
        bool ciaddrSet = !req.CiAddr.Equals(IPAddress.Any);
        IPAddress ip;
        string state;

        if (serverId != null)
        {
            // SELECTING：客户端在多个 OFFER 里选了一个
            state = "SELECTING";
            if (!serverId.Equals(_cfg.ServerIp))
            {
                _offers.Remove(mac);
                log = Loc.T("dhcpEngine.requestFromClientChoseAnother", mac, serverId);
                return null;
            }
            var want = requested ?? (ciaddrSet ? req.CiAddr : null);
            if (want == null) { log = Loc.T("dhcpEngine.requestSelectingFromRequestedAddress", mac); return null; }
            ip = want;
            if (!AvailableFor(ip, mac)) return Nak(req, mac, NakReason(ip), state, out log);
        }
        else if (requested != null && !ciaddrSet)
        {
            // INIT-REBOOT：客户端重连后确认以前的地址
            state = "INIT-REBOOT";
            ip = requested;
            if (!OnSubnet(ip)) return Nak(req, mac, NakReason(ip), state, out log);
            if (!AvailableFor(ip, mac)) return Nak(req, mac, NakReason(ip), state, out log);
        }
        else if (ciaddrSet)
        {
            // RENEWING（单播）/ REBINDING（广播）：续租
            state = "RENEWING/REBINDING";
            ip = req.CiAddr;
            if (!OnSubnet(ip)) return Nak(req, mac, NakReason(ip), state, out log);
            if (!AvailableFor(ip, mac)) return Nak(req, mac, NakReason(ip), state, out log);
        }
        else
        {
            log = Loc.T("dhcpEngine.requestFromNeitherCiaddrNor", mac);
            return null;
        }

        var prev = _store.GetByMac(mac);
        bool renewal = prev != null && prev.Ip == ip.ToString() && prev.IsActive(now);
        var lease = new DhcpLease
        {
            Mac = mac,
            Ip = ip.ToString(),
            ClientId = ClientIdHex(req) ?? prev?.ClientId,
            HostName = req.GetStringOption(DhcpOption.HostName) ?? prev?.HostName,
            VendorClass = req.GetStringOption(DhcpOption.VendorClassId) ?? prev?.VendorClass,
            Expires = now + _cfg.LeaseTime,
            LastSeen = now,
            Released = false,
        };
        _store.Upsert(lease);
        _offers.Remove(mac);
        t.Acks++;
        t.LastAck = now;
        var reply = BuildReply(req, DhcpMessageType.Ack, ip);
        log = Loc.T("dhcpEngine.requestFromAckLeaseH", state, mac, HostSuffix(req), ip, _cfg.LeaseTime.TotalHours);
        var ev = new LeaseEvent(renewal ? LeaseEventKind.Renewed : LeaseEventKind.Granted, lease.Copy());
        Commit(() => Raise(ev));
        return new DhcpReply(reply, Destination(req), "ACK " + ip);
    }

    private (string zh, string en) NakReason(IPAddress ip)
    {
        if (!OnSubnet(ip)) return (Loc.T("dhcpEngine.notHotspotSubnet", ip), "wrong network");
        if (!InPool(ip)) return (Loc.T("dhcpEngine.notAddressPool", ip), "address not in pool");
        if (_declined.ContainsKey(NetworkInfo.ToUInt32(ip))) return (Loc.T("dhcpEngine.wasJustReportedAsConflicting", ip), "address declined");
        return (Loc.T("dhcpEngine.assignedAnotherDevice", ip), "address in use");
    }

    private DhcpReply Nak(DhcpPacket req, string mac, (string zh, string en) why, string state, out string log)
    {
        Trace(mac).Naks++;
        _offers.Remove(mac);
        var p = NewReply(req, DhcpMessageType.Nak);
        p.YiAddr = IPAddress.Any;
        p.CiAddr = IPAddress.Any;
        p.SetOption(DhcpOption.ServerId, _cfg.ServerIp);
        p.SetOption(DhcpOption.Message, Encoding.ASCII.GetBytes(why.en));
        EchoClientId(req, p);
        p.BroadcastFlag = true;
        log = Loc.T("dhcpEngine.requestFromNak", state, mac, why.zh);
        return new DhcpReply(p, IPAddress.Broadcast, "NAK");
    }

    private DhcpReply? OnDecline(DhcpPacket req, string mac, out string log)
    {
        Trace(mac);
        var ip = req.GetIpOption(DhcpOption.RequestedIp);
        var serverId = req.GetIpOption(DhcpOption.ServerId);
        if (serverId != null && !serverId.Equals(_cfg.ServerIp)) { log = Loc.T("dhcpEngine.declineFromMeantAnotherServer", mac); return null; }
        if (ip == null) { log = Loc.T("dhcpEngine.declineFromNoAddressIgnored", mac); return null; }
        _declined[NetworkInfo.ToUInt32(ip)] = _clock() + DeclineBlock;
        _offers.Remove(mac);
        var lease = _store.GetByMac(mac);
        if (lease != null && lease.Ip == ip.ToString())
        {
            _store.Remove(mac);
            var ev = new LeaseEvent(LeaseEventKind.Declined, lease);
            Commit(() => Raise(ev));
        }
        log = Loc.T("dhcpEngine.declineFromAlreadyUsedBy", mac, ip);
        return null;
    }

    private DhcpReply? OnRelease(DhcpPacket req, string mac, out string log)
    {
        Trace(mac);
        var lease = _store.GetByMac(mac);
        if (lease != null && lease.Ip == req.CiAddr.ToString() && !lease.Released)
        {
            lease.Released = true;
            lease.Expires = _clock();
            lease.LastSeen = _clock();
            _store.Upsert(lease);
            var ev = new LeaseEvent(LeaseEventKind.Released, lease.Copy());
            Commit(() => Raise(ev));
            log = Loc.T("dhcpEngine.releaseFromReleased", mac, req.CiAddr);
        }
        else log = Loc.T("dhcpEngine.releaseFromNoMatchingLease", mac, req.CiAddr);
        return null;
    }

    private DhcpReply? OnInform(DhcpPacket req, string mac, out string log)
    {
        Trace(mac);
        if (req.CiAddr.Equals(IPAddress.Any)) { log = Loc.T("dhcpEngine.informFromCiaddrEmptyIgnored", mac); return null; }
        var p = NewReply(req, DhcpMessageType.Ack);
        p.CiAddr = req.CiAddr;
        p.YiAddr = IPAddress.Any;
        AddNetworkOptions(p);
        EchoClientId(req, p);
        log = Loc.T("dhcpEngine.informFromAckNetworkParameters", mac, req.CiAddr);
        return new DhcpReply(p, req.CiAddr, "ACK(INFORM)");
    }

    // ---------------------------------------------------------------- 构造应答

    private static DhcpPacket NewReply(DhcpPacket req, DhcpMessageType type)
    {
        var p = new DhcpPacket
        {
            Op = 2,
            HType = req.HType,
            HLen = req.HLen,
            Hops = 0,
            Xid = req.Xid,
            Secs = 0,
            Flags = req.Flags,
            GiAddr = req.GiAddr,
            SiAddr = IPAddress.Any,
            ChAddr = (byte[])req.ChAddr.Clone(),
        };
        p.MessageType = type;
        return p;
    }

    private DhcpPacket BuildReply(DhcpPacket req, DhcpMessageType type, IPAddress ip)
    {
        var p = NewReply(req, type);
        p.YiAddr = ip;
        p.CiAddr = type == DhcpMessageType.Ack && !req.CiAddr.Equals(IPAddress.Any) ? req.CiAddr : IPAddress.Any;
        p.SetOption(DhcpOption.ServerId, _cfg.ServerIp);
        var secs = (uint)Math.Max(60, _cfg.LeaseTime.TotalSeconds);
        p.SetOptionUInt32(DhcpOption.LeaseTime, secs);
        p.SetOptionUInt32(DhcpOption.RenewalTime, secs / 2);
        p.SetOptionUInt32(DhcpOption.RebindingTime, (uint)(secs * 0.875));
        AddNetworkOptions(p);
        EchoClientId(req, p);
        return p;
    }

    private void AddNetworkOptions(DhcpPacket p)
    {
        p.SetOption(DhcpOption.ServerId, _cfg.ServerIp);
        p.SetOption(DhcpOption.SubnetMask, _cfg.SubnetMask);
        p.SetOption(DhcpOption.Router, _cfg.Router);
        // 仅局域网：不下发 DNS / 域名（也从不下发选项 121/249 之类的额外路由）
        if (_cfg.AllowInternet && _cfg.Dns.Count > 0) p.SetOption(DhcpOption.DnsServers, _cfg.Dns);
        p.SetOption(DhcpOption.BroadcastAddress, _cfg.BroadcastAddress);
        if (_cfg.AllowInternet && !string.IsNullOrEmpty(_cfg.DomainName)) p.SetOption(DhcpOption.DomainName, Encoding.ASCII.GetBytes(_cfg.DomainName));
    }

    /// <summary>RFC 6842：应答里必须原样带回客户端标识（选项 61）。</summary>
    private static void EchoClientId(DhcpPacket req, DhcpPacket reply)
    {
        var cid = req.GetOption(DhcpOption.ClientId);
        if (cid is { Length: > 0 }) reply.SetOption(DhcpOption.ClientId, cid);
    }

    private static string? ClientIdHex(DhcpPacket req)
    {
        var cid = req.GetOption(DhcpOption.ClientId);
        return cid is { Length: > 0 } ? Convert.ToHexString(cid) : null;
    }

    private static string HostSuffix(DhcpPacket req)
    {
        var h = req.GetStringOption(DhcpOption.HostName);
        return h == null ? "" : Loc.T("common.paren", h);
    }

    /// <summary>
    /// RFC 2131 4.1 的发送规则：ciaddr 非 0 时单播给 ciaddr；否则广播
    /// （用户态程序无法在客户端还没有 IP 时按 MAC 单播，所以即使广播标志没置位也广播）。NAK 另行处理，一律广播。
    /// </summary>
    private static IPAddress Destination(DhcpPacket req) =>
        req.CiAddr.Equals(IPAddress.Any) ? IPAddress.Broadcast : req.CiAddr;

    /// <summary>界面/日志用：当前所有租约（按 IP 排序）。</summary>
    public List<DhcpLease> GetLeases() =>
        _store.All().OrderBy(l => IPAddress.TryParse(l.Ip, out var a) ? NetworkInfo.ToUInt32(a) : 0).ToList();
}

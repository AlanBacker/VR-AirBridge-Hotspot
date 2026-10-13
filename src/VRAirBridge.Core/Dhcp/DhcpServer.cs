using System.Net;
using System.Net.Sockets;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.Dhcp;

public sealed class DhcpServerOptions
{
    /// <summary>热点网卡的 IP（例如 192.168.137.1），应答从这个地址发出。</summary>
    public required IPAddress ServerIp { get; init; }
    /// <summary>热点网卡的 IPv4 接口索引；只处理从这块网卡收到的报文。0 = 不过滤（测试用）。</summary>
    public int InterfaceIndex { get; init; }
    public int ServerPort { get; init; } = 67;
    public int ClientPort { get; init; } = 68;
    /// <summary>除了 ServerIp:67，再监听 0.0.0.0:67（按接口索引过滤），确保收得到广播。</summary>
    public bool BindAny { get; init; } = true;
    /// <summary>测试用：把“广播”应答改发到这个地址（例如 127.0.0.1）。</summary>
    public IPAddress? BroadcastOverride { get; init; }
}

/// <summary>
/// 内置 DHCP 服务器的网络部分。
/// 接收：套接字 A 绑定 热点IP:67（同时负责发送，开 SO_BROADCAST，出口锁定到热点网卡）；
///       套接字 B 绑定 0.0.0.0:67（只收，按 IP_PKTINFO 的接口索引过滤，避免处理 Hyper-V 等别的网卡上的 DHCP）。
/// 同一个广播包两个套接字都会收到，用内容哈希去重。任何一个绑定失败都能单独工作。
/// </summary>
public sealed class DhcpServer : IDisposable
{
    private const int SioUdpConnReset = -1744830452; // 0x9800000C
    private const int IpUnicastIf = 31;

    private readonly DhcpEngine _engine;
    private readonly DhcpServerOptions _opt;
    private readonly object _dedupeLock = new();
    private readonly Dictionary<ulong, (DateTime Time, int Sock)> _recent = new();
    private Socket? _specific;
    private Socket? _any;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _loops = new();
    private DateTime _lastForeignLog = DateTime.MinValue;

    public DhcpServer(DhcpEngine engine, DhcpServerOptions options)
    {
        _engine = engine;
        _opt = options;
    }

    public DhcpEngine Engine => _engine;
    public DhcpServerOptions Options => _opt;
    public bool IsRunning { get; private set; }
    public string BindText { get; private set; } = "";
    public long Received => Interlocked.Read(ref _received);
    public long Sent => Interlocked.Read(ref _sent);
    public long SendErrors => Interlocked.Read(ref _sendErrors);
    public DateTime? LastReceivedUtc { get; private set; }
    private long _received, _sent, _sendErrors;

    /// <summary>启动；失败时抛出带中文说明的异常。</summary>
    public void Start()
    {
        if (IsRunning) return;
        var errors = new List<string>();
        try
        {
            _specific = NewSocket();
            _specific.EnableBroadcast = true;
            if (_opt.InterfaceIndex > 0) SetUnicastIf(_specific, _opt.InterfaceIndex);
            _specific.Bind(new IPEndPoint(_opt.ServerIp, _opt.ServerPort));
        }
        catch (SocketException ex)
        {
            errors.Add(Loc.T("dhcpServer.bindFailed", _opt.ServerIp, _opt.ServerPort, ex.SocketErrorCode));
            _specific?.Dispose();
            _specific = null;
        }

        if (_opt.BindAny)
        {
            try
            {
                _any = NewSocket();
                _any.EnableBroadcast = true;
                if (_opt.InterfaceIndex > 0) SetUnicastIf(_any, _opt.InterfaceIndex);
                _any.Bind(new IPEndPoint(IPAddress.Any, _opt.ServerPort));
            }
            catch (SocketException ex)
            {
                errors.Add(Loc.T("dhcpServer.bind0000", _opt.ServerPort, ex.SocketErrorCode));
                _any?.Dispose();
                _any = null;
            }
        }

        if (_specific == null && _any == null)
            throw new InvalidOperationException(Loc.T("dhcpServer.dhcpPortUseOrCannot") + string.Join(Loc.T("common.listSep"), errors));

        var parts = new List<string>();
        if (_specific != null) parts.Add($"{_opt.ServerIp}:{_opt.ServerPort}");
        if (_any != null) parts.Add(Loc.T("dhcpServer.n0000Hotspot", _opt.ServerPort));
        BindText = string.Join(" + ", parts);
        foreach (var e in errors) Logger.Debug(Loc.T("dhcpServer.dhcpOtherSocketKeepsWorking", e));

        _cts = new CancellationTokenSource();
        IsRunning = true;
        if (_specific != null) _loops.Add(Task.Run(() => ReceiveLoop(_specific, 1, _cts.Token)));
        if (_any != null) _loops.Add(Task.Run(() => ReceiveLoop(_any, 2, _cts.Token)));
        var cfg = _engine.Config;
        Logger.Ok(Loc.T("dhcpServer.builtDhcpServerStartedListening", BindText, cfg.PoolStart, cfg.PoolEnd, cfg.Router, string.Join(", ", cfg.Dns)));
    }

    private static Socket NewSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch { }
        return s;
    }

    private static void SetUnicastIf(Socket s, int ifIndex)
    {
        try { s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(ifIndex)); }
        catch (Exception ex) { Logger.Debug(Loc.T("dhcpServer.failedSetIpUnicastIf") + ex.Message); }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _specific?.Close(); } catch { }
        try { _any?.Close(); } catch { }
        try { Task.WaitAll(_loops.ToArray(), 2000); } catch { }
        _loops.Clear();
        _specific = null;
        _any = null;
        _cts?.Dispose();
        _cts = null;
        Logger.Info(Loc.T("dhcpServer.builtDhcpServerStopped"));
    }

    public void Dispose() => Stop();

    private async Task ReceiveLoop(Socket sock, int id, CancellationToken ct)
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult r;
            try
            {
                r = await sock.ReceiveMessageFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Logger.Warn(Loc.T("dhcpServer.dhcpReceiveErrorRetrying1", ex.SocketErrorCode));
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            try { Process(sock, id, buf.AsSpan(0, r.ReceivedBytes).ToArray(), (IPEndPoint)r.RemoteEndPoint, r.PacketInformation); }
            catch (Exception ex) { Logger.Debug(Loc.T("dhcpServer.errorHandlingDhcpPacket") + ex.Message); }
        }
    }

    private void Process(Socket sock, int sockId, byte[] data, IPEndPoint remote, IPPacketInformation pi)
    {
        if (_opt.InterfaceIndex > 0 && pi.Interface != _opt.InterfaceIndex)
        {
            if (DateTime.UtcNow - _lastForeignLog > TimeSpan.FromMinutes(1))
            {
                _lastForeignLog = DateTime.UtcNow;
                Logger.Debug(Loc.T("dhcpServer.dhcpIgnoredPacketFromAnother", pi.Interface, remote));
            }
            return;
        }
        if (IsDuplicate(data, sockId)) return;

        Interlocked.Increment(ref _received);
        LastReceivedUtc = DateTime.UtcNow;

        DhcpPacket req;
        try { req = DhcpPacket.Parse(data); }
        catch (FormatException ex)
        {
            Logger.Debug(Loc.T("dhcpServer.dhcpReceivedUnparsablePacketBytes", data.Length, remote, ex.Message));
            return;
        }

        var host = req.GetStringOption(DhcpOption.HostName);
        var reqIp = req.GetIpOption(DhcpOption.RequestedIp);
        Logger.Debug(Loc.T("dhcpServer.dhcpXid0xFromInterface", DhcpPacket.TypeName(req.MessageType), req.ClientMac, req.Xid, remote, pi.Address, pi.Interface) +
                     (req.CiAddr.Equals(IPAddress.Any) ? "" : $" ciaddr={req.CiAddr}") +
                     (reqIp == null ? "" : Loc.T("dhcpServer.requested", reqIp)) +
                     (host == null ? "" : Loc.T("dhcpServer.hostname", host)) +
                     (req.BroadcastFlag ? Loc.T("dhcpServer.broadcastFlag") : ""));

        var reply = _engine.Handle(req, out var log);
        var level = req.MessageType switch
        {
            DhcpMessageType.Decline => LogLevel.Warn,
            DhcpMessageType.Request when reply?.Packet.MessageType == DhcpMessageType.Ack => LogLevel.Info,
            DhcpMessageType.Request when reply?.Packet.MessageType == DhcpMessageType.Nak => LogLevel.Info,
            _ => LogLevel.Debug,
        };
        Logger.Write(level, "DHCP: " + log);
        if (reply == null) return;
        Send(reply);
    }

    private bool IsDuplicate(byte[] data, int sockId)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in data) { h ^= b; h *= 1099511628211UL; }
        var now = DateTime.UtcNow;
        lock (_dedupeLock)
        {
            if (_recent.Count > 256)
                foreach (var k in _recent.Where(kv => now - kv.Value.Time > TimeSpan.FromSeconds(3)).Select(kv => kv.Key).ToList()) _recent.Remove(k);
            if (_recent.TryGetValue(h, out var prev) && prev.Sock != sockId && now - prev.Time < TimeSpan.FromSeconds(1))
                return true;
            _recent[h] = (now, sockId);
            return false;
        }
    }

    private void Send(DhcpReply reply)
    {
        var bytes = reply.Packet.ToBytes();
        bool isBroadcast = reply.Destination.Equals(IPAddress.Broadcast);
        var dst = isBroadcast ? (_opt.BroadcastOverride ?? IPAddress.Broadcast) : reply.Destination;
        var targets = new List<IPEndPoint> { new(dst, _opt.ClientPort) };
        var sock = _specific ?? _any;
        if (sock == null) return;
        // 只有 0.0.0.0 套接字可用时，再补发一份子网定向广播，保证从热点网卡发出
        if (_specific == null && isBroadcast && _opt.BroadcastOverride == null)
            targets.Add(new IPEndPoint(_engine.Config.BroadcastAddress, _opt.ClientPort));
        foreach (var ep in targets)
        {
            try
            {
                sock.SendTo(bytes, ep);
                Interlocked.Increment(ref _sent);
                Logger.Debug(Loc.T("dhcpServer.dhcpXid0xBytes", reply.Description, reply.Packet.ClientMac, reply.Packet.Xid, ep, bytes.Length));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _sendErrors);
                Logger.Warn(Loc.T("dhcpServer.failedSendDhcpReply", reply.Description, ep, ex.Message));
            }
        }
    }
}

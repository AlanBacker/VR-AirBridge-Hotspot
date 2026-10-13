using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace VRAirBridge.Core.Dhcp;

public enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}

/// <summary>RFC 2132 选项编号。</summary>
public static class DhcpOption
{
    public const byte Pad = 0;
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte DnsServers = 6;
    public const byte HostName = 12;
    public const byte DomainName = 15;
    public const byte BroadcastAddress = 28;
    public const byte RequestedIp = 50;
    public const byte LeaseTime = 51;
    public const byte Overload = 52;
    public const byte MessageType = 53;
    public const byte ServerId = 54;
    public const byte ParameterRequestList = 55;
    public const byte Message = 56;
    public const byte MaxMessageSize = 57;
    public const byte RenewalTime = 58;
    public const byte RebindingTime = 59;
    public const byte VendorClassId = 60;
    public const byte ClientId = 61;
    public const byte End = 255;
}

/// <summary>
/// DHCP/BOOTP 报文（RFC 2131 第 2 节格式）。解析时会处理选项重复拼接（RFC 3396）和 option 52 覆盖 sname/file。
/// </summary>
public sealed class DhcpPacket
{
    public static readonly byte[] MagicCookie = { 99, 130, 83, 99 };
    public const int FixedHeaderLength = 236;
    public const int MinBootpLength = 300;

    public byte Op { get; set; } = 1;
    public byte HType { get; set; } = 1;
    public byte HLen { get; set; } = 6;
    public byte Hops { get; set; }
    public uint Xid { get; set; }
    public ushort Secs { get; set; }
    public ushort Flags { get; set; }
    public IPAddress CiAddr { get; set; } = IPAddress.Any;
    public IPAddress YiAddr { get; set; } = IPAddress.Any;
    public IPAddress SiAddr { get; set; } = IPAddress.Any;
    public IPAddress GiAddr { get; set; } = IPAddress.Any;
    public byte[] ChAddr { get; set; } = new byte[16];
    public byte[] SName { get; set; } = new byte[64];
    public byte[] File { get; set; } = new byte[128];

    /// <summary>选项（按加入顺序输出）。</summary>
    public List<KeyValuePair<byte, byte[]>> Options { get; } = new();

    public bool BroadcastFlag
    {
        get => (Flags & 0x8000) != 0;
        set => Flags = value ? (ushort)(Flags | 0x8000) : (ushort)(Flags & 0x7FFF);
    }

    public string ClientMac
    {
        get
        {
            int len = Math.Clamp((int)HLen, 0, 16);
            if (len == 0) len = 6;
            return string.Join("-", ChAddr.Take(len).Select(b => b.ToString("X2")));
        }
    }

    public byte[]? GetOption(byte code)
    {
        foreach (var kv in Options) if (kv.Key == code) return kv.Value;
        return null;
    }

    public void SetOption(byte code, byte[] value)
    {
        for (int i = 0; i < Options.Count; i++)
        {
            if (Options[i].Key == code) { Options[i] = new(code, value); return; }
        }
        Options.Add(new(code, value));
    }

    public void SetOption(byte code, IPAddress ip) => SetOption(code, ip.GetAddressBytes());

    public void SetOption(byte code, IEnumerable<IPAddress> ips) => SetOption(code, ips.SelectMany(i => i.GetAddressBytes()).ToArray());

    public void SetOptionUInt32(byte code, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        SetOption(code, b);
    }

    public DhcpMessageType? MessageType
    {
        get
        {
            var v = GetOption(DhcpOption.MessageType);
            return v is { Length: >= 1 } && v[0] >= 1 && v[0] <= 8 ? (DhcpMessageType)v[0] : null;
        }
        set
        {
            if (value.HasValue) SetOption(DhcpOption.MessageType, new[] { (byte)value.Value });
        }
    }

    public IPAddress? GetIpOption(byte code)
    {
        var v = GetOption(code);
        return v is { Length: >= 4 } ? new IPAddress(v.AsSpan(0, 4)) : null;
    }

    public uint? GetUInt32Option(byte code)
    {
        var v = GetOption(code);
        return v is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32BigEndian(v) : null;
    }

    public string? GetStringOption(byte code)
    {
        var v = GetOption(code);
        if (v == null || v.Length == 0) return null;
        var s = Encoding.UTF8.GetString(v).TrimEnd('\0');
        var sb = new StringBuilder();
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        var r = sb.ToString().Trim();
        if (r.Length > 64) r = r[..64];
        return r.Length == 0 ? null : r;
    }

    public static DhcpPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FixedHeaderLength + 4) throw new FormatException(Loc.T("dhcpPacket.packetTooShortBytes", data.Length));
        var p = new DhcpPacket
        {
            Op = data[0],
            HType = data[1],
            HLen = data[2],
            Hops = data[3],
            Xid = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            Secs = BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            CiAddr = new IPAddress(data.Slice(12, 4)),
            YiAddr = new IPAddress(data.Slice(16, 4)),
            SiAddr = new IPAddress(data.Slice(20, 4)),
            GiAddr = new IPAddress(data.Slice(24, 4)),
            ChAddr = data.Slice(28, 16).ToArray(),
            SName = data.Slice(44, 64).ToArray(),
            File = data.Slice(108, 128).ToArray(),
        };
        if (!data.Slice(236, 4).SequenceEqual(MagicCookie)) throw new FormatException(Loc.T("dhcpPacket.dhcpMagicCookieMissingBootp"));

        var raw = new List<KeyValuePair<byte, byte[]>>();
        int overload = ParseOptions(data[240..], raw);
        if ((overload & 1) != 0) ParseOptions(p.File, raw);
        if ((overload & 2) != 0) ParseOptions(p.SName, raw);

        // RFC 3396：同一选项出现多次时拼接
        foreach (var g in raw.GroupBy(kv => kv.Key))
        {
            var bytes = g.SelectMany(kv => kv.Value).ToArray();
            p.Options.Add(new(g.Key, bytes));
        }
        return p;
    }

    /// <summary>解析一段选项区，返回 option 52 的值（0 表示没有）。</summary>
    private static int ParseOptions(ReadOnlySpan<byte> s, List<KeyValuePair<byte, byte[]>> into)
    {
        int overload = 0;
        int i = 0;
        while (i < s.Length)
        {
            byte code = s[i];
            if (code == DhcpOption.Pad) { i++; continue; }
            if (code == DhcpOption.End) break;
            if (i + 1 >= s.Length) break;
            int len = s[i + 1];
            if (i + 2 + len > s.Length) break;
            var val = s.Slice(i + 2, len).ToArray();
            if (code == DhcpOption.Overload && len >= 1) overload = val[0];
            else into.Add(new(code, val));
            i += 2 + len;
        }
        return overload;
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream(576);
        Span<byte> hdr = stackalloc byte[FixedHeaderLength];
        hdr.Clear();
        hdr[0] = Op; hdr[1] = HType; hdr[2] = HLen; hdr[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], Xid);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[8..], Secs);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[10..], Flags);
        CiAddr.GetAddressBytes().CopyTo(hdr[12..]);
        YiAddr.GetAddressBytes().CopyTo(hdr[16..]);
        SiAddr.GetAddressBytes().CopyTo(hdr[20..]);
        GiAddr.GetAddressBytes().CopyTo(hdr[24..]);
        ChAddr.AsSpan(0, Math.Min(16, ChAddr.Length)).CopyTo(hdr[28..]);
        SName.AsSpan(0, Math.Min(64, SName.Length)).CopyTo(hdr[44..]);
        File.AsSpan(0, Math.Min(128, File.Length)).CopyTo(hdr[108..]);
        ms.Write(hdr);
        ms.Write(MagicCookie);
        foreach (var kv in Options)
        {
            var v = kv.Value;
            int off = 0;
            // 超过 255 字节的选项按 RFC 3396 拆分
            do
            {
                int chunk = Math.Min(255, v.Length - off);
                ms.WriteByte(kv.Key);
                ms.WriteByte((byte)chunk);
                ms.Write(v, off, chunk);
                off += chunk;
            } while (off < v.Length);
        }
        ms.WriteByte(DhcpOption.End);
        while (ms.Length < MinBootpLength) ms.WriteByte(0);
        return ms.ToArray();
    }

    public static string TypeName(DhcpMessageType? t) => t switch
    {
        DhcpMessageType.Discover => "DISCOVER",
        DhcpMessageType.Offer => "OFFER",
        DhcpMessageType.Request => "REQUEST",
        DhcpMessageType.Decline => "DECLINE",
        DhcpMessageType.Ack => "ACK",
        DhcpMessageType.Nak => "NAK",
        DhcpMessageType.Release => "RELEASE",
        DhcpMessageType.Inform => "INFORM",
        _ => "BOOTP",
    };
}

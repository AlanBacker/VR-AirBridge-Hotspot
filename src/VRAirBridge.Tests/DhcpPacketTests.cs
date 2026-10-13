using System.Buffers.Binary;
using System.Net;
using VRAirBridge.Core.Dhcp;
using Xunit;

namespace VRAirBridge.Tests;

public class DhcpPacketTests
{
    [Fact]
    public void RoundTrip_PreservesHeaderAndOptions()
    {
        var p = Pkt.Discover(7, host: "iPhone", requested: IPAddress.Parse("192.168.137.150"));
        p.BroadcastFlag = true;
        p.Secs = 3;
        var bytes = p.ToBytes();
        Assert.True(bytes.Length >= 300);
        Assert.Equal(new byte[] { 99, 130, 83, 99 }, bytes.AsSpan(236, 4).ToArray());

        var q = DhcpPacket.Parse(bytes);
        Assert.Equal(1, q.Op);
        Assert.Equal(0x1234u, q.Xid);
        Assert.Equal(3, q.Secs);
        Assert.True(q.BroadcastFlag);
        Assert.Equal(DhcpMessageType.Discover, q.MessageType);
        Assert.Equal(Pkt.MacText(7), q.ClientMac);
        Assert.Equal("iPhone", q.GetStringOption(DhcpOption.HostName));
        Assert.Equal(IPAddress.Parse("192.168.137.150"), q.GetIpOption(DhcpOption.RequestedIp));
        Assert.Equal(7, q.GetOption(DhcpOption.ClientId)!.Length);
    }

    [Fact]
    public void Parse_RejectsShortOrCookieless()
    {
        Assert.Throws<FormatException>(() => DhcpPacket.Parse(new byte[100]));
        var bytes = Pkt.Discover(1).ToBytes();
        bytes[236] = 0;
        Assert.Throws<FormatException>(() => DhcpPacket.Parse(bytes));
    }

    [Fact]
    public void LongOption_IsSplitAndConcatenated_Rfc3396()
    {
        var p = Pkt.Discover(1);
        var big = Enumerable.Range(0, 600).Select(i => (byte)(i % 251)).ToArray();
        p.SetOption(43, big);
        var q = DhcpPacket.Parse(p.ToBytes());
        Assert.Equal(big, q.GetOption(43));
    }

    [Fact]
    public void Parse_HandlesOptionOverloadInFileField()
    {
        // 手工构造：选项区只有 53 和 52(=1 → file 字段里还有选项)，主机名放在 file 字段
        var p = Pkt.Client(DhcpMessageType.Discover, 3);
        p.Options.Clear();
        p.MessageType = DhcpMessageType.Discover;
        p.SetOption(DhcpOption.Overload, new byte[] { 1 });
        var bytes = p.ToBytes();
        var file = new byte[128];
        file[0] = DhcpOption.HostName; file[1] = 5;
        "quest"u8.CopyTo(file.AsSpan(2));
        file[7] = DhcpOption.End;
        Array.Copy(file, 0, bytes, 108, 128);
        var q = DhcpPacket.Parse(bytes);
        Assert.Equal("quest", q.GetStringOption(DhcpOption.HostName));
    }

    [Fact]
    public void HostName_IsSanitized()
    {
        var p = Pkt.Discover(1);
        p.SetOption(DhcpOption.HostName, new byte[] { (byte)'a', 0x07, (byte)'b', 0, 0 });
        Assert.Equal("ab", Pkt.Wire(p).GetStringOption(DhcpOption.HostName));
    }

    [Fact]
    public void UInt32Option_IsBigEndian()
    {
        var p = new DhcpPacket();
        p.SetOptionUInt32(DhcpOption.LeaseTime, 43200);
        Assert.Equal(43200u, BinaryPrimitives.ReadUInt32BigEndian(p.GetOption(DhcpOption.LeaseTime)));
        Assert.Equal(43200u, p.GetUInt32Option(DhcpOption.LeaseTime));
    }
}

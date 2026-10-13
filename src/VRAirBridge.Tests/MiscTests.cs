using System.Net;
using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;
using Xunit;

namespace VRAirBridge.Tests;

public class MacUtilTests
{
    [Theory]
    [InlineData("bc:22:28:c8:6d:36", "BC2228C86D36")]
    [InlineData("BC-22-28-C8-6D-36", "BC2228C86D36")]
    [InlineData("BC2228C86D36", "BC2228C86D36")]
    [InlineData("xyz", "")]
    [InlineData(null, "")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, MacUtil.Normalize(input));

    [Fact]
    public void SameRadio_ComparesLastFiveOctets()
    {
        // Wi-Fi Direct GO MAC = 物理网卡 MAC 改第一个字节（本地管理位）
        Assert.True(MacUtil.SameRadio("BC-22-28-C8-6D-36", "BE-22-28-C8-6D-36"));
        Assert.True(MacUtil.SameRadio("bc:22:28:c8:6d:36", "FE-22-28-C8-6D-36"));
        Assert.False(MacUtil.SameRadio("BC-22-28-C8-6D-36", "BE-22-28-C8-6D-37"));
        Assert.False(MacUtil.SameRadio("", "BE-22-28-C8-6D-37"));
    }

    [Fact]
    public void Last4_And_Format()
    {
        Assert.Equal("6D36", MacUtil.Last4("bc:22:28:c8:6d:36"));
        Assert.Equal("BC-22-28-C8-6D-36", MacUtil.Format("bc2228c86d36"));
        Assert.Equal("01-02-03-04-05-06", MacUtil.Format(new byte[] { 1, 2, 3, 4, 5, 6, 0, 0 }));
    }
}

public class BeaconParserTests
{
    private static byte[] Ie(int id, params byte[] data) => new[] { (byte)id, (byte)data.Length }.Concat(data).ToArray();

    private static byte[] HeCap(int nss)
    {
        var d = new byte[1 + 6 + 11 + 4];
        d[0] = 35;
        // Rx HE-MCS map ≤80 MHz：每条空间流 2 位，0 = MCS 0-7 支持，3 = 不支持
        int map = 0;
        for (int k = 0; k < 8; k++) map |= (k < nss ? 0 : 3) << (2 * k);
        d[18] = (byte)(map & 0xFF);
        d[19] = (byte)(map >> 8);
        return Ie(255, d);
    }

    [Fact]
    public void Parses_5GHz_80MHz_ax_2ss_AsFull()
    {
        var ies = Ie(0, "AirBridge-6D36"u8.ToArray())
            .Concat(Ie(45, new byte[26]))
            .Concat(Ie(61, 36, 0x05, 0, 0, 0, 0)) // 主信道 36，次信道在上方 → 40
            .Concat(Ie(191, 0, 0, 0, 0, 0xFA, 0xFF, 0, 0, 0xFA, 0xFF, 0, 0)) // VHT cap: 2SS
            .Concat(Ie(192, 1, 42, 0, 0, 0))   // VHT op: 80 MHz, CCFS0 = 42
            .Concat(HeCap(2))
            .Concat(Ie(255, 36, 0, 0, 0, 0, 0, 0))
            .ToArray();
        var b = BeaconParser.Parse(ies, 5180000);
        Assert.Equal(36, b.Channel);
        Assert.Equal(5180, b.FreqMHz);
        Assert.Equal(80, b.WidthMHz);
        Assert.Equal("802.11ax", b.Phy);
        Assert.Equal(2, b.Streams);
        Assert.True(b.IsFull);
    }

    [Fact]
    public void Parses_160MHz()
    {
        var ies = Ie(61, 36, 0x05, 0, 0, 0, 0).Concat(Ie(192, 1, 42, 50, 0, 0)).Concat(HeCap(2)).ToArray();
        Assert.Equal(160, BeaconParser.Parse(ies, 5180000).WidthMHz);
    }

    [Fact]
    public void Parses_2_4GHz_n_1ss_AsNotFull()
    {
        var ht = new byte[26];
        ht[3] = 0xFF; // MCS 0-7 → 1 条空间流
        var ies = Ie(45, ht).Concat(Ie(61, 6, 0, 0, 0, 0, 0)).ToArray();
        var b = BeaconParser.Parse(ies, 2437000);
        Assert.Equal(6, b.Channel);
        Assert.Equal(20, b.WidthMHz);
        Assert.Equal("802.11n", b.Phy);
        Assert.Equal(1, b.Streams);
        Assert.False(b.IsFull);
    }

    [Fact]
    public void Tolerates_TruncatedIes()
    {
        var ies = Ie(61, 36, 0x05, 0, 0, 0, 0).Concat(new byte[] { 192, 40, 1 }).ToArray();
        var b = BeaconParser.Parse(ies, 5180000);
        Assert.Equal(36, b.Channel);
        Assert.Equal(40, b.WidthMHz);
    }
}

public class SettingsTests
{
    [Theory]
    [InlineData("AirBridge-6D36", "abcd1234", null)]
    [InlineData("", "abcd1234", "热点名称不能为空")]
    [InlineData("AirBridge", "short", "密码长度必须是 8–63 位")]
    [InlineData("AirBridge", "密码密码密码密码", "密码只能包含英文字母、数字和英文符号")]
    public void Validate(string ssid, string pw, string? expected) => Assert.Equal(expected, AppSettings.Validate(ssid, pw));

    [Fact]
    public void Validate_SsidByteLimit()
    {
        Assert.NotNull(AppSettings.Validate(new string('热', 11), "abcd1234")); // 33 字节
        Assert.Null(AppSettings.Validate(new string('热', 10), "abcd1234"));    // 30 字节
    }

    [Fact]
    public void GeneratePassword_IsValidAndRandom()
    {
        var a = AppSettings.GeneratePassword(12);
        var b = AppSettings.GeneratePassword(12);
        Assert.Equal(12, a.Length);
        Assert.NotEqual(a, b);
        Assert.Null(AppSettings.Validate("x", a));
        Assert.DoesNotContain(a, c => "0O1lI".Contains(c));
    }

    [Fact]
    public void Store_RoundTrip_AndDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrab-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = store.Load();
            Assert.True(store.IsFirstRun);
            Assert.Equal("5GHz", s.Band);
            Assert.Equal("auto", s.Dhcp.Mode);
            Assert.Equal(12, s.Dhcp.LeaseHours);
            Assert.Equal("VRAirBridgeNat", s.Nat.Name);
            s.Ssid = "AirBridge-测试";
            s.Revert.DisabledAdapters.Add("vEthernet (Default Switch)");
            store.Save(s);
            var json = File.ReadAllText(store.ConfigPath);
            Assert.Contains("AirBridge-测试", json); // 中文不转义，方便手动编辑
            var store2 = new SettingsStore(dir);
            var s2 = store2.Load();
            Assert.False(store2.IsFirstRun);
            Assert.Equal("AirBridge-测试", s2.Ssid);
            Assert.Single(s2.Revert.DisabledAdapters);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Store_CorruptConfig_IsBackedUp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrab-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ this is not json");
            var s = new SettingsStore(dir).Load();
            Assert.Equal("5GHz", s.Band);
            Assert.Contains(Directory.GetFiles(dir), f => Path.GetFileName(f).StartsWith("config.json.bad-"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class NetConfigTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void EngineConfig_RebasesPoolOntoGoSubnet()
    {
        // ICS 把网段改成了 192.168.138.0/24 时，地址池 .100–.199 跟着搬过去
        var cfg = DhcpEngineConfig.Create(Ip("192.168.138.1"), Ip("255.255.255.0"), new DhcpSettings(), new[] { Ip("8.8.8.8") });
        Assert.Equal(Ip("192.168.138.100"), cfg.PoolStart);
        Assert.Equal(Ip("192.168.138.199"), cfg.PoolEnd);
        Assert.Equal(Ip("192.168.138.1"), cfg.Router);
        Assert.Equal(Ip("192.168.138.255"), cfg.BroadcastAddress);
        Assert.Equal(100, cfg.PoolSize);
        Assert.Equal(TimeSpan.FromHours(12), cfg.LeaseTime);
    }

    [Fact]
    public void EngineConfig_ClampsBadPool()
    {
        var s = new DhcpSettings { PoolStart = "192.168.137.0", PoolEnd = "192.168.137.255" };
        var cfg = DhcpEngineConfig.Create(Ip("192.168.137.1"), Ip("255.255.255.0"), s, Array.Empty<IPAddress>());
        Assert.Equal(Ip("192.168.137.1"), cfg.PoolStart); // 服务器地址本身由 InPool 排除
        Assert.Equal(Ip("192.168.137.254"), cfg.PoolEnd);
        var e = new DhcpEngine(cfg, new DhcpLeaseStore(null));
        Assert.False(e.InPool(Ip("192.168.137.1")));
        Assert.True(e.InPool(Ip("192.168.137.2")));
    }

    [Theory]
    [InlineData("192.168.137.0/24", "192.168.137.55", true)]
    [InlineData("192.168.137.0/24", "192.168.138.55", false)]
    [InlineData("192.168.0.0/16", "192.168.137.1", true)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("garbage", "192.168.137.1", false)]
    public void NatPrefixContains(string prefix, string ip, bool expected) =>
        Assert.Equal(expected, NatManager.PrefixContains(prefix, Ip(ip)));

    [Fact]
    public void NatPrefixOf() => Assert.Equal("192.168.137.0/24", NatManager.PrefixOf(Ip("192.168.137.1"), Ip("255.255.255.0")));

    [Fact]
    public void SubnetHelpers()
    {
        Assert.True(NetworkInfo.IsApipa(Ip("169.254.10.20")));
        Assert.False(NetworkInfo.IsApipa(Ip("192.168.137.1")));
        Assert.Equal(24, NetworkInfo.PrefixLength(Ip("255.255.255.0")));
        Assert.True(NetworkInfo.SameSubnet(Ip("192.168.137.1"), Ip("192.168.137.200"), Ip("255.255.255.0")));
    }

    [Fact]
    public void ChooseDns_UsesUplinkThenFallback_WhenNoLocalDns()
    {
        var go = new GoAdapterInfo("本地连接* 10", "Microsoft Wi-Fi Direct Virtual Adapter #2", "{X}", "BE-22-28-C8-6D-36", 99, Ip("192.168.137.1"), Ip("255.255.255.0"));
        var up = new UplinkInfo("WLAN", "Intel Wi-Fi", "{Y}", 12, Ip("192.168.1.5"), Ip("192.168.1.1"), new[] { Ip("192.168.1.1"), Ip("fe80::1") });
        var dns = DhcpService.ChooseDns(go, up, new[] { "223.5.5.5", "8.8.8.8", "bogus" });
        // 本机 192.168.137.1:53 有没有监听取决于测试机器（ICS 运行时有）；两种情况都要合理
        if (dns[0].Equals(Ip("192.168.137.1"))) Assert.Single(dns);
        else
        {
            Assert.Equal(new[] { Ip("192.168.1.1"), Ip("223.5.5.5"), Ip("8.8.8.8") }, dns);
        }
    }
}

public class LoggerTests
{
    [Fact]
    public void Secrets_AreMasked()
    {
        var seen = new List<string>();
        void H(LogEntry e) => seen.Add(e.Message);
        Logger.EntryWritten += H;
        try
        {
            Logger.RegisterSecret("Sup3rS3cret!");
            Logger.Info("password is Sup3rS3cret! ok");
            Assert.Contains(seen, m => m == "password is ****** ok");
            Assert.DoesNotContain(seen, m => m.Contains("Sup3rS3cret!"));
        }
        finally
        {
            Logger.EntryWritten -= H;
            Logger.ForgetSecret("Sup3rS3cret!");
        }
    }
}

public class DongleProbeTests
{
    [Fact]
    public void UsbProbe_FindsDongleHardware_WhenPlugged()
    {
        // 依赖测试机器：插着 D-Link 时，PnP 查询（WQL LIKE 'USB\VID_2001&PID_3323%'）必须能找到它
        var d = VRAirBridge.Core.Hotspot.DongleInfo.Find();
        var usb = VRAirBridge.Core.Hotspot.DongleInfo.FindUsbWithoutDriver();
        if (d != null && d.PnpId.StartsWith(VRAirBridge.Core.Hotspot.DongleInfo.HardwareIdPrefix, StringComparison.OrdinalIgnoreCase))
            Assert.NotNull(usb);
    }
}

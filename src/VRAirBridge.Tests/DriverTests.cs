using System.Security.Cryptography;
using System.Text;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.SystemSetup;
using Xunit;

namespace VRAirBridge.Tests;

public class DriverTests
{
    private const string DongleId = @"USB\VID_2001&PID_3323\00E04C000001";

    private static DriverPackage Bundled() => DriverPackage.FromAssembly(typeof(DriverTests).Assembly);

    // ---------------------------------------------------------------- pnputil 退出码

    [Fact]
    public void PnpUtil_0_Installed_WithPublishedName_AndBannerRemoved()
    {
        var r = DriverInstaller.MapPnpUtil(0,
            "Microsoft PnP 工具\r\n\r\n正在添加驱动程序包:  netrtwlanufb.inf\r\n已成功添加驱动程序包。\r\n已发布名称:         oem42.inf\r\n已在设备上安装驱动程序包。\r\n", "");
        Assert.Equal(PnpUtilOutcome.Installed, r.Outcome);
        Assert.True(r.Ok);
        Assert.Equal("oem42.inf", r.PublishedName);
        Assert.Equal(DriverStrings.Installed, r.Message);
        Assert.DoesNotContain("Microsoft PnP", r.Output);
    }

    [Fact]
    public void PnpUtil_259_StagedNoDevice_IsOk()
    {
        var r = DriverInstaller.MapPnpUtil(259, "Microsoft PnP Utility\n\nAdding driver package:  netrtwlanufb.inf\nDriver package added successfully.\nPublished Name:         oem42.inf\n", null);
        Assert.Equal(PnpUtilOutcome.StagedNoDevice, r.Outcome);
        Assert.True(r.Ok);
        Assert.Equal("驱动已放入系统，插上网卡会自动装", r.Message);
        Assert.Equal("oem42.inf", r.PublishedName);
    }

    [Fact]
    public void PnpUtil_3010_RebootRequired_IsOk()
    {
        var r = DriverInstaller.MapPnpUtil(3010, "", "");
        Assert.Equal(PnpUtilOutcome.RebootRequired, r.Outcome);
        Assert.True(r.Ok);
        Assert.Contains("重启", r.Message);
    }

    [Fact]
    public void PnpUtil_2_InvalidPackage_FailsWithPnpUtilText()
    {
        const string fixture = "Microsoft PnP 工具\n\n无法添加驱动程序包: 指定的驱动程序包缺失或无效。\n\n驱动程序包总数:  0\n已添加驱动程序包数:  0";
        var r = DriverInstaller.MapPnpUtil(2, fixture, "");
        Assert.Equal(PnpUtilOutcome.Failed, r.Outcome);
        Assert.False(r.Ok);
        Assert.StartsWith(DriverStrings.FailedPrefix, r.Message);
        Assert.Contains("退出码 2", r.Message);
        Assert.Contains("指定的驱动程序包缺失或无效", r.Message);
        Assert.Contains("驱动程序包总数:  0", r.Output);
        Assert.DoesNotContain("Microsoft PnP", r.Output);
        Assert.Null(r.PublishedName);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(740)]
    public void PnpUtil_AccessDenied_SaysAdmin(int code)
    {
        var r = DriverInstaller.MapPnpUtil(code, "", "Access is denied.");
        Assert.False(r.Ok);
        Assert.Contains("管理员", r.Message);
    }

    [Fact]
    public void PnpUtil_Timeout_Fails()
    {
        var r = DriverInstaller.MapPnpUtil(0, "", "", timedOut: true);
        Assert.False(r.Ok);
        Assert.Contains("超时", r.Message);
    }

    // ---------------------------------------------------------------- 内置驱动包

    [Fact]
    public void BundledManifest_HasTenFiles_AndVerifies()
    {
        var pkg = Bundled();
        var m = pkg.Manifest;
        Assert.Equal(10, m.Files.Count);
        Assert.Equal(DriverStrings.BundledVersion, m.DriverVersion);
        Assert.Equal(DriverStrings.BundledDate, m.DriverDate);
        Assert.Equal("netrtwlanufb.inf", m.Inf);
        Assert.Contains(m.Files, f => f.Name == "rtwlanufb.sys");
        Assert.Empty(pkg.Verify());
    }

    /// <summary>把某个内置文件的一个字节改掉。</summary>
    private static DriverPackage Tampered(string victim, int offset = 1000)
    {
        var asm = typeof(DriverTests).Assembly;
        return new DriverPackage(name =>
        {
            var s = asm.GetManifestResourceStream(DriverPackage.ResourcePrefix + name);
            if (s == null || name != victim) return s;
            var ms = new MemoryStream();
            using (s) s.CopyTo(ms);
            var bytes = ms.ToArray();
            bytes[Math.Min(offset, bytes.Length - 1)] ^= 0x5A;
            return new MemoryStream(bytes);
        });
    }

    [Fact]
    public void Verify_TamperedByte_Fails()
    {
        var problems = Tampered("rtwlanufb.sys").Verify();
        Assert.Single(problems);
        Assert.Contains("rtwlanufb.sys", problems[0]);
        Assert.Contains("SHA-256", problems[0]);
    }

    [Fact]
    public void Verify_MissingFile_Fails()
    {
        var asm = typeof(DriverTests).Assembly;
        var pkg = new DriverPackage(name => name == "netrtwlanufb.cat" ? null : asm.GetManifestResourceStream(DriverPackage.ResourcePrefix + name));
        var problems = pkg.Verify();
        Assert.Single(problems);
        Assert.Contains("netrtwlanufb.cat", problems[0]);
    }

    [Fact]
    public void ExtractTo_TempDir_WritesTenFilesWithCorrectHashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrab-drv-test-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, "driver");
        try
        {
            var pkg = Bundled();
            Assert.Equal(10, pkg.ExtractTo(dir));
            var files = Directory.GetFiles(dir);
            Assert.Equal(10, files.Length);
            foreach (var f in pkg.Manifest.Files)
            {
                var path = Path.Combine(dir, f.Name);
                Assert.True(File.Exists(path), f.Name);
                var bytes = File.ReadAllBytes(path);
                Assert.Equal(f.Length, bytes.LongLength);
                Assert.Equal(f.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            // 再解压一次（覆盖旧目录）也没问题，而且不留临时目录
            Assert.Equal(10, pkg.ExtractTo(dir));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ExtractTo_Tampered_Throws_AndLeavesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrab-drv-test-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, "driver");
        try
        {
            Assert.Throws<DriverPackageException>(() => Tampered("netrtwlanufb.inf", 500).ExtractTo(dir));
            Assert.False(Directory.Exists(dir));
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void BundledInf_DriverVer_MatchesManifest()
    {
        using var s = typeof(DriverTests).Assembly.GetManifestResourceStream(DriverPackage.ResourcePrefix + "netrtwlanufb.inf")!;
        using var reader = new StreamReader(s, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var (date, version) = DriverInstaller.ParseDriverVer(reader.ReadToEnd());
        Assert.Equal(DriverStrings.BundledVersion, version);
        Assert.Equal(DriverStrings.BundledDate, date);
    }

    [Theory]
    [InlineData("20230831000000.******+000", "2023-08-31")]
    [InlineData("08/31/2023", "2023-08-31")]
    [InlineData("8/1/2023", "2023-08-01")]
    [InlineData("2023-08-31", "2023-08-31")]
    [InlineData("garbage", "garbage")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void NormalizeDate_Formats(string? input, string? expected) =>
        Assert.Equal(expected, DriverInstaller.NormalizeDate(input));

    [Fact]
    public void ParseDriverVer_NoMatch_ReturnsNulls()
    {
        Assert.Equal((null, null), DriverInstaller.ParseDriverVer("[Version]\nSignature=\"$WINDOWS NT$\"\n"));
        Assert.Equal(("2023-08-31", "1.27.831.2023"), DriverInstaller.ParseDriverVer("[Version]\r\n  DriverVer = 08/31/2023,1.27.831.2023 ; comment\r\n"));
    }

    // ---------------------------------------------------------------- 状态判断（模拟 WMI 数据）

    private static readonly PnpDeviceRow NoDriverRow = new(DongleId, "802.11ax WLAN Adapter", null, null, 28, true);

    [Fact]
    public void Classify_Nothing_IsNotPresent()
    {
        var st = DriverInstaller.Classify(Array.Empty<PnpDeviceRow>(), Array.Empty<SignedDriverRow>(), Array.Empty<NetAdapterRow>());
        Assert.Equal(DriverStateKind.DongleNotPresent, st.Kind);
        Assert.False(st.DonglePresent);
        Assert.False(st.IsInstalled);
    }

    [Fact]
    public void Classify_UsbDeviceWithoutAdapter_IsPresentNoDriver()
    {
        var st = DriverInstaller.Classify(new[] { NoDriverRow, new PnpDeviceRow(@"USB\VID_046D&PID_C52B\5&1", "USB Receiver", null, "HidUsb", 0, true) },
            Array.Empty<SignedDriverRow>(), Array.Empty<NetAdapterRow>());
        Assert.Equal(DriverStateKind.PresentNoDriver, st.Kind);
        Assert.True(st.DonglePresent);
        Assert.False(st.IsInstalled);
        Assert.Equal("802.11ax WLAN Adapter", st.DeviceName);
        Assert.Equal(28, st.ErrorCode);
        Assert.Equal(DriverStrings.KindPresentNoDriver, st.ShortText);
        Assert.Contains(st.DescribeLines(), l => l.Contains(DriverStrings.NoDriverHint));
    }

    [Fact]
    public void Classify_PhantomDevice_IsNotPresent()
    {
        var st = DriverInstaller.Classify(new[] { NoDriverRow with { Present = false } }, Array.Empty<SignedDriverRow>(), Array.Empty<NetAdapterRow>());
        Assert.Equal(DriverStateKind.DongleNotPresent, st.Kind);
    }

    [Fact]
    public void Classify_AdapterWithBundledVersion_IsOk()
    {
        var st = DriverInstaller.Classify(
            new[] { NoDriverRow with { ErrorCode = 0, Service = "rtwlanufb" } },
            Array.Empty<SignedDriverRow>(),
            new[] { new NetAdapterRow("WLAN 2", "D-Link DWA-F18 AX1800 Wi-Fi USB Adapter", DongleId, "1.27.831.2023", "8/31/2023", "Realtek Semiconductor Corp.") });
        Assert.Equal(DriverStateKind.DriverOk, st.Kind);
        Assert.True(st.IsInstalled);
        Assert.Equal("WLAN 2", st.AdapterName);
        Assert.Equal("2023-08-31", st.Date);
        Assert.Equal(0, st.ErrorCode);
    }

    [Fact]
    public void Classify_AdapterWithOtherVersion_IsOtherVersion()
    {
        var st = DriverInstaller.Classify(Array.Empty<PnpDeviceRow>(), Array.Empty<SignedDriverRow>(),
            new[] { new NetAdapterRow("WLAN 2", "Realtek 8852AU", DongleId, "1.19.6.0", null, "Realtek") });
        Assert.Equal(DriverStateKind.DriverOtherVersion, st.Kind);
        Assert.True(st.IsInstalled);
        Assert.Contains(DriverStrings.BundledVersion, st.ShortText);
    }

    [Fact]
    public void Classify_VersionFromSignedDriver_WhenAdapterHasNone()
    {
        var st = DriverInstaller.Classify(Array.Empty<PnpDeviceRow>(),
            new[] { new SignedDriverRow(DongleId.ToLowerInvariant(), "1.27.831.2023", "oem42.inf", "20230831000000.******+000", "Realtek") },
            new[] { new NetAdapterRow("WLAN 2", "D-Link DWA-F18", DongleId, null, null, null) });
        Assert.Equal(DriverStateKind.DriverOk, st.Kind);
        Assert.Equal("2023-08-31", st.Date);
        Assert.Equal("Realtek", st.Provider);
        Assert.EndsWith(Path.Combine("INF", "oem42.inf"), st.InfPath);
    }

    [Fact]
    public void Classify_AdapterMatchedByDescription_WhenPnpIdDiffers()
    {
        var st = DriverInstaller.Classify(Array.Empty<PnpDeviceRow>(), Array.Empty<SignedDriverRow>(),
            new[] { new NetAdapterRow("WLAN 3", "D-Link DWA-F18 AX1800", @"USB\VID_0BDA&PID_8832\X", "1.27.831.2023", null, null) });
        Assert.Equal(DriverStateKind.DriverOk, st.Kind);
    }

    [Fact]
    public void FromProbe_UsesUsbProbeWhenNoAdapter()
    {
        Assert.Equal(DriverStateKind.PresentNoDriver, DriverInstaller.FromProbe(null, "802.11ax WLAN Adapter").Kind);
        Assert.Equal(DriverStateKind.DongleNotPresent, DriverInstaller.FromProbe(null, null).Kind);
    }

    [Fact]
    public void NoDriverHint_PointsToBuiltInDriver_NotMeta()
    {
        Assert.Equal("点击「安装驱动」即可（软件已内置 D-Link/Realtek 驱动 1.27.831.2023）", DriverStrings.NoDriverHint);
        Assert.Equal(DriverStrings.NoDriverHint, DongleInfo.NoDriverHint);
        foreach (var s in new[] { DriverStrings.NoDriverHint, DriverStrings.InlineWarning, DriverStrings.StartNeedsDriver, DriverStrings.PromptText })
        {
            Assert.DoesNotContain("Meta", s);
            Assert.DoesNotContain("Oculus", s);
        }
    }

    // ---------------------------------------------------------------- Meta 残留

    [Fact]
    public void ParseEnumDrivers_Chinese()
    {
        const string text = "Microsoft PnP 工具\r\n\r\n" +
            "发布名称:     oem26.inf\r\n原始名称:      oculus_vigembus.inf\r\n提供程序名称:      Facebook, Inc.\r\n类名:         系统设备\r\n" +
            "类 GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}\r\n驱动程序版本:     08/02/2017 1.5.8.0\r\n签名者名称:     Microsoft Windows Hardware Compatibility Publisher\r\n\r\n" +
            "发布名称:     oem3.inf\r\n原始名称:      netrtwlanufb.inf\r\n提供程序名称:      Realtek Semiconductor Corp.\r\n类名:         网络适配器\r\n" +
            "类 GUID:         {4d36e972-e325-11ce-bfc1-08002be10318}\r\n驱动程序版本:     08/31/2023 1.27.831.2023\r\n签名者名称:     Microsoft Windows Hardware Compatibility Publisher\r\n";
        var list = MetaLeftovers.ParseEnumDrivers(text);
        Assert.Equal(2, list.Count);
        Assert.Equal(new DriverStoreEntry("oem26.inf", "oculus_vigembus.inf", "Facebook, Inc.", "2017-08-02 1.5.8.0"), list[0]);
        Assert.Equal("oem3.inf", list[1].PublishedName);
        Assert.Equal("2023-08-31 1.27.831.2023", list[1].Version);
        Assert.True(MetaLeftovers.IsMetaDriver(list[0]));
        Assert.False(MetaLeftovers.IsMetaDriver(list[1]));
    }

    [Fact]
    public void ParseEnumDrivers_English()
    {
        const string text = "Microsoft PnP Utility\n\nPublished Name:     oem26.inf\nOriginal Name:      oculus_vigembus.inf\nProvider Name:      Facebook, Inc.\n" +
            "Class Name:         System devices\nClass GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}\nDriver Version:     08/02/2017 1.5.8.0\nSigner Name:        Microsoft Windows Hardware Compatibility Publisher\n";
        var e = Assert.Single(MetaLeftovers.ParseEnumDrivers(text));
        Assert.Equal("oculus_vigembus.inf", e.OriginalName);
        Assert.Equal("2017-08-02 1.5.8.0", e.Version);
    }

    [Theory]
    [InlineData(166999583L, "159.3 MB")]
    [InlineData(2056551L, "2.0 MB")]
    [InlineData(3997696L, "3.8 MB")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(512L, "512 B")]
    public void FormatSize(long bytes, string expected) => Assert.Equal(expected, MetaLeftovers.FormatSize(bytes));
}

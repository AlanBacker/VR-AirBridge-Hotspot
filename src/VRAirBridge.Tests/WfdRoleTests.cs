using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Wlan;
using Xunit;

namespace VRAirBridge.Tests;

public class WfdRoleTests
{
    private static WlanAdapter Adapter(string name, string pnp, bool connected = false) =>
        new(name, name + " adapter", "{" + Guid.NewGuid() + "}", "", pnp, 2, connected ? 1 : 0);

    [Fact]
    public void AdaptersToPause_OnlyTheHolder_WhenItCanBeSeen()
    {
        // 2026-10-13 作者电脑：Windows 把 Wi-Fi Direct 给了内置网卡
        var others = new[] { Adapter("WLAN", @"PCI\VEN_10EC&DEV_C852\1", connected: true), Adapter("WLAN 3", @"USB\VID_0BDA&PID_8812\2") };
        var pause = WfdRole.AdaptersToPause(others, a => a.Name == "WLAN" ? 2 : 0);
        Assert.Equal(new[] { "WLAN" }, pause.Select(a => a.Name));
    }

    [Fact]
    public void AdaptersToPause_AllOthers_WhenNobodyVisiblyHoldsIt()
    {
        var others = new[] { Adapter("WLAN", "a"), Adapter("WLAN 3", "b") };
        Assert.Equal(new[] { "WLAN", "WLAN 3" }, WfdRole.AdaptersToPause(others, _ => 0).Select(a => a.Name));
    }

    [Fact]
    public void WfdPorts_UnknownDevice_IsZero()
    {
        Assert.Equal(0, WfdRole.WfdPorts(null));
        Assert.Equal(0, WfdRole.WfdPorts(@"USB\VID_FFFF&PID_FFFF\NOPE"));
    }
}

public class ReceptionCheckTests
{
    private static WlanBss Bss(string bssid, int rssi, int mhz) => new("ssid", bssid, rssi, (uint)mhz * 1000, Array.Empty<byte>());

    [Fact]
    public void DeafDongle_OnUsbHub_IsWeak()
    {
        // 2026-10-13 作者电脑：D-Link 插在集线器上时，内置网卡 −44 dBm 的路由器它只收到 −90 左右
        var reference = new[] { Bss("AA:00:00:00:00:01", -44, 5745), Bss("AA:00:00:00:00:02", -58, 5180), Bss("AA:00:00:00:00:03", -66, 5500) };
        var dongle = new[] { Bss("AA:00:00:00:00:01", -90, 5745) };
        var r = ReceptionCheck.Compare(dongle, reference)!;
        Assert.Equal(2, r.Samples);           // −66 的那个 D-Link 没扫到也不算（参考网卡本身也不算很清楚）
        Assert.Equal((46 + 45) / 2, r.MedianDelta);
        Assert.True(r.Weak);
        Assert.Equal(-90, r.DongleBest);
        Assert.Equal(-44, r.ReferenceBest);
    }

    [Fact]
    public void GoodPort_IsNotWeak()
    {
        // 换到主板背面的口以后：−48 ~ −53 对 −41
        var reference = new[] { Bss("AA:00:00:00:00:01", -41, 5745), Bss("AA:00:00:00:00:02", -55, 5180), Bss("AA:00:00:00:00:03", -70, 5500) };
        var dongle = new[] { Bss("aa-00-00-00-00-01", -50, 5745), Bss("AA:00:00:00:00:02", -60, 5180), Bss("AA:00:00:00:00:03", -78, 5500) };
        var r = ReceptionCheck.Compare(dongle, reference)!;
        Assert.Equal(3, r.Samples);
        Assert.Equal(8, r.MedianDelta);
        Assert.False(r.Weak);
    }

    [Fact]
    public void OnlyFiveGhz_AndOnlyRoutersTheReferenceHearsWell()
    {
        var reference = new[] { Bss("AA:00:00:00:00:01", -40, 2437), Bss("AA:00:00:00:00:02", -80, 5180), Bss("AA:00:00:00:00:03", -50, 5955) };
        Assert.Null(ReceptionCheck.Compare(Array.Empty<WlanBss>(), reference));
    }

    [Fact]
    public void SingleSample_IsNeverWeak()
    {
        var r = ReceptionCheck.Compare(Array.Empty<WlanBss>(), new[] { Bss("AA:00:00:00:00:01", -45, 5745) })!;
        Assert.Equal(1, r.Samples);
        Assert.Equal(45, r.MedianDelta);
        Assert.Null(r.DongleBest);
        Assert.False(r.Weak);
    }

    [Fact]
    public void StrongestReadingPerBssid_IsUsed()
    {
        var reference = new[] { Bss("AA:00:00:00:00:01", -70, 5745), Bss("AA:00:00:00:00:01", -45, 5745), Bss("AA:00:00:00:00:02", -50, 5180) };
        var dongle = new[] { Bss("AA:00:00:00:00:01", -60, 5745), Bss("AA:00:00:00:00:01", -50, 5745), Bss("AA:00:00:00:00:02", -55, 5180) };
        var r = ReceptionCheck.Compare(dongle, reference)!;
        Assert.Equal(5, r.MedianDelta);
    }
}

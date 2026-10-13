using System.Xml.Linq;
using VRAirBridge.Core;
using VRAirBridge.Core.Hotspot;
using Xunit;

namespace VRAirBridge.Tests;

public class ChannelTests
{
    private static NeighborBss N(string ssid, int ch, int width, int dbm, string bssid = "") =>
        new(ssid, bssid.Length > 0 ? bssid : $"AA-BB-CC-00-{ch:X2}-{Math.Abs(dbm):X2}", ch, ch <= 14 ? 2407 + ch * 5 : 5000 + ch * 5, width, dbm, "802.11ax");

    [Theory]
    [InlineData(36, "36–48")]
    [InlineData(48, "36–48")]
    [InlineData(64, "52–64")]
    [InlineData(100, "100–112")]
    [InlineData(144, "132–144")]
    [InlineData(149, "149–161")]
    [InlineData(161, "149–161")]
    public void BlockOf_5GHz(int ch, string name) => Assert.Equal(name, ChannelInfo.BlockOf(ch)?.Name);

    [Theory]
    [InlineData(6)]
    [InlineData(165)]
    [InlineData(0)]
    public void BlockOf_OutsideBlocks_IsNull(int ch) => Assert.Null(ChannelInfo.BlockOf(ch));

    [Theory]
    [InlineData(36, false)]
    [InlineData(48, false)]
    [InlineData(52, true)]
    [InlineData(100, true)]
    [InlineData(144, true)]
    [InlineData(149, false)]
    [InlineData(165, false)]
    public void IsDfs(int ch, bool dfs) => Assert.Equal(dfs, ChannelInfo.IsDfs(ch));

    [Fact]
    public void BlocksCovered_80And160()
    {
        Assert.Equal(new[] { "36–48" }, ChannelInfo.BlocksCovered(40, 80).Select(b => b.Name));
        Assert.Equal(new[] { "36–48", "52–64" }, ChannelInfo.BlocksCovered(36, 160).Select(b => b.Name));
        Assert.Equal(new[] { "36–48", "52–64" }, ChannelInfo.BlocksCovered(60, 160).Select(b => b.Name));
        Assert.Equal(new[] { "100–112", "116–128" }, ChannelInfo.BlocksCovered(116, 160).Select(b => b.Name));
        Assert.Equal(new[] { "149–161" }, ChannelInfo.BlocksCovered(149, 160).Select(b => b.Name));
        Assert.Empty(ChannelInfo.BlocksCovered(6, 20));
    }

    [Fact]
    public void Occupancy_CountsPerBlock_AndFormats()
    {
        var neighbors = new[]
        {
            N("HomeWiFi", 36, 80, -50),
            N("Neighbor-A", 44, 80, -70),
            N("Neighbor-B", 100, 80, -60),
            N("Neighbor-C", 149, 20, -80),
            N("Wide160", 36, 160, -65),
        };
        var occ = ChannelInfo.Occupancy(neighbors);
        Assert.Equal(6, occ.Count);
        var b36 = occ.Single(o => o.Block.First == 36);
        Assert.Equal(3, b36.Networks);
        Assert.Equal(-50, b36.StrongestDbm);
        Assert.Equal(1, occ.Single(o => o.Block.First == 52).Networks);
        Assert.Equal(1, occ.Single(o => o.Block.First == 100).Networks);
        Assert.Equal(0, occ.Single(o => o.Block.First == 116).Networks);
        Assert.Null(occ.Single(o => o.Block.First == 116).StrongestDbm);
        Assert.Equal(1, occ.Single(o => o.Block.First == 149).Networks);

        Assert.Equal("36–48：3 个（最强 -50 dBm） · 149–161：1 个（最强 -80 dBm） · 52–144：DFS，热点不会使用（共 2 个）",
            ChannelInfo.FormatOccupancy(occ));
    }

    [Fact]
    public void Occupancy_Empty_AllFree()
    {
        Assert.Equal("36–48：空闲 · 149–161：空闲 · 52–144：DFS，热点不会使用",
            ChannelInfo.FormatOccupancy(ChannelInfo.Occupancy(Array.Empty<NeighborBss>())));
    }

    [Fact]
    public void BuildCandidates_UplinkFirst_ExcludesOwnHotspotAndHidden()
    {
        var neighbors = new[]
        {
            N("HomeWiFi", 36, 80, -40),
            N("HomeWiFi", 149, 80, -60),
            N("Neighbor5G", 100, 80, -70),
            N("", 44, 80, -55),
            N("VR-Hotspot-1234", 44, 80, -30),
            N("Cafe", 157, 80, -75),
        };
        var list = ChannelInfo.BuildCandidates(neighbors, "HomeWiFi", 36, new HashSet<string> { "HomeWiFi", "Neighbor5G" }, "VR-Hotspot-1234");
        Assert.Equal(new[] { "HomeWiFi", "Neighbor5G", "Cafe" }.OrderBy(x => x), list.Select(c => c.Ssid).OrderBy(x => x));
        var home = list[0];
        Assert.Equal("HomeWiFi", home.Ssid);
        Assert.True(home.IsUplink);
        Assert.Equal(36, home.Channel);
        Assert.True(home.Usable);
        Assert.Contains(ChannelStrings.CandidateUplink, home.Display);
        Assert.Equal(1, home.Display.Count(ch => ch is '(' or '\uFF08'));   // one bracket, qualifiers joined by a separator
        Assert.Equal(1, home.Display.Count(ch => ch is ')' or '\uFF09'));

        var dfs = list.Single(c => c.Ssid == "Neighbor5G");
        Assert.True(dfs.Dfs);
        Assert.False(dfs.Usable);
        Assert.Contains(ChannelStrings.CandidateDfs, dfs.Display);

        var cafe = list.Single(c => c.Ssid == "Cafe");
        Assert.False(cafe.HasProfile);
        Assert.False(cafe.Usable);
        Assert.Contains(ChannelStrings.CandidateNoProfile, cafe.Display);
    }

    [Fact]
    public void BuildCandidates_Uplink24GHz_NotUsable()
    {
        var list = ChannelInfo.BuildCandidates(Array.Empty<NeighborBss>(), "Home-2.4G", 6, new HashSet<string> { "Home-2.4G" }, null);
        var c = Assert.Single(list);
        Assert.True(c.Is24GHz);
        Assert.False(c.Usable);
        Assert.Contains(ChannelStrings.Candidate24, c.Display);
    }

    [Fact]
    public void CandidateQualifiers_HaveNoBracketsOfTheirOwn()
    {
        string[] keys = { "channelInfo.channel", "channel.candidateUplink", "channel.candidateDfs", "channel.candidate24", "channel.candidateNoProfile", "channel.candidateSep" };
        foreach (var lang in Loc.Codes)
            foreach (var k in keys)
                Assert.DoesNotContain(Loc.Lookup(lang, k), ch => ch is '(' or ')' or '\uFF08' or '\uFF09');
    }

    // ---------------------------------------------------------------- 钉住信道：配置文件 XML（假数据，不是真实密钥）

    private const string ProfileXml =
        "<?xml version=\"1.0\"?>\n" +
        "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\n" +
        "  <name>HomeWiFi-profile</name>\n" +
        "  <SSIDConfig><SSID><hex>486F6D6557694669</hex><name>HomeWiFi</name></SSID></SSIDConfig>\n" +
        "  <connectionType>ESS</connectionType>\n" +
        "  <connectionMode>auto</connectionMode>\n" +
        "  <MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
        "<sharedKey><keyType>passPhrase</keyType><protected>true</protected><keyMaterial>01000000D08C9DDF0115D1118C7A00C04FC297EB</keyMaterial></sharedKey></security></MSM>\n" +
        "</WLANProfile>";

    [Fact]
    public void Profile_SsidAndName()
    {
        Assert.Equal("HomeWiFi", ChannelPin.ProfileSsid(ProfileXml));
        Assert.Equal("HomeWiFi-profile", ChannelPin.ProfileName(ProfileXml));
        Assert.Null(ChannelPin.ProfileSsid(null));
        Assert.Null(ChannelPin.ProfileSsid("<not xml"));
        Assert.Null(ChannelPin.ProfileName(""));
    }

    [Fact]
    public void MakeManual_ChangesOnlyConnectionMode()
    {
        var manual = ChannelPin.MakeManual(ProfileXml);
        Assert.Contains("<connectionMode>manual</connectionMode>", manual);
        Assert.DoesNotContain("<connectionMode>auto</connectionMode>", manual);
        Assert.Equal(ProfileXml.Replace("<connectionMode>auto</connectionMode>", "<connectionMode>manual</connectionMode>"), manual);
        // 已经是 manual：原样返回
        Assert.Equal(manual, ChannelPin.MakeManual(manual));
    }

    [Fact]
    public void MakeManual_InsertsWhenMissing_AndStaysValidXml()
    {
        var noMode = ProfileXml.Replace("  <connectionMode>auto</connectionMode>\n", "");
        var r = ChannelPin.MakeManual(noMode);
        var doc = XDocument.Parse(r);
        var mode = doc.Descendants().Single(e => e.Name.LocalName == "connectionMode");
        Assert.Equal("manual", mode.Value);
        Assert.Equal("MSM", ((XElement)mode.NextNode!).Name.LocalName);
        Assert.Equal("HomeWiFi", ChannelPin.ProfileSsid(r));
    }
}

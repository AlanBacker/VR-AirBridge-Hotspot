using VRAirBridge.Core;
using Xunit;

namespace VRAirBridge.Tests;

public class DisplayTextTests
{
    private const char WJ = DisplayText.WordJoiner;

    [Theory]
    [InlineData("D-Link DWA-F18", "D\u2011Link DWA\u2011F18")]
    [InlineData("WPA2-Personal", "WPA2\u2011Personal")]
    [InlineData("RSSI -48 dBm", "RSSI -48 dBm")]   // a minus after a space stays a normal minus
    [InlineData("a - b", "a - b")]                 // a spaced dash is a real break opportunity
    public void Hyphens_InsideTokens_DoNotBreak(string input, string expected) =>
        Assert.Equal(expected, DisplayText.Shape(input));

    [Fact]
    public void Korean_KeepsWordsWhole_BreaksOnlyAtSpaces()
    {
        var s = DisplayText.Shape("\uC778\uD130\uB137: \uD5C8\uC6A9\uB428");
        Assert.Equal($"\uC778{WJ}\uD130{WJ}\uB137{WJ}: \uD5C8{WJ}\uC6A9{WJ}\uB428", s);
        Assert.Equal("\uC778\uD130\uB137: \uD5C8\uC6A9\uB428", s.Replace(WJ.ToString(), ""));
    }

    [Fact]
    public void ShortBracketGroup_IsGlued_LongOneIsNot()
    {
        Assert.Equal("80 MHz (1\u00A0total)", DisplayText.Shape("80 MHz (1 total)"));
        const string longGroup = "(this bracket has far more than sixteen characters)";
        Assert.Equal(longGroup, DisplayText.Shape(longGroup));
    }

    [Theory]
    [InlineData("demo-pass-2468")]
    [InlineData("Plain text, nothing to do")]
    [InlineData("\u4E2D\u6587\uFF08\u4E0A\u7F51\uFF09")]
    public void Shape_LeavesOtherCharactersAlone(string input) =>
        Assert.Equal(input.Replace('-', DisplayText.NonBreakingHyphen), DisplayText.Shape(input));

    [Fact]
    public void NullAndShort_AreSafe()
    {
        Assert.Equal("", DisplayText.Shape(null));
        Assert.Equal("a", DisplayText.Shape("a"));
    }
}

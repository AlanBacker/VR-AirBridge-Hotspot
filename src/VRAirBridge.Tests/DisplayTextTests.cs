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
        Assert.Equal($"\uC778{WJ}\uD130{WJ}\uB137: \uD5C8{WJ}\uC6A9{WJ}\uB428", s);
        Assert.Equal("\uC778\uD130\uB137: \uD5C8\uC6A9\uB428", s.Replace(WJ.ToString(), ""));
    }

    [Fact]
    public void Korean_NoJoinerWhereALineCannotBreakAnyway()
    {
        // after an opening bracket, before closing punctuation, next to a no-break space
        Assert.Equal($"[\uC815{WJ}\uBCF4] (\uAC00{WJ}\uC7A5\u00A0\uAC15{WJ}\uD55C)",
            DisplayText.Shape("[\uC815\uBCF4] (\uAC00\uC7A5 \uAC15\uD55C)"));
        // a line may still break between a digit or a Latin letter and Hangul
        Assert.Equal($"2{WJ}\uAC1C ICS{WJ}\uAC00", DisplayText.Shape("2\uAC1C ICS\uAC00"));
    }

    [Theory]
    [InlineData("channel 100/80  -80 dBm  DFS", "channel\u00A0100/80\u00A0\u00A0\u201180\u00A0dBm\u00A0\u00A0DFS")]
    [InlineData("\u4FE1\u9053 36/80  -48 dBm", "\u4FE1\u2060\u9053\u00A036/80\u00A0\u00A0\u201148\u00A0dBm")]
    [InlineData("\u30C1\u30E3\u30F3 1", "\u30C1\u2060\u30E3\u2060\u30F3\u00A01")]
    [InlineData("a/b 1.5", "a/\u2060b\u00A01.5")]
    public void KeepTogether_JoinsOnlyWhereALineCouldBreak(string input, string expected) =>
        Assert.Equal(expected, DisplayText.KeepTogether(input));

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

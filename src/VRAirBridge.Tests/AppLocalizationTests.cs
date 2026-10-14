using System.Text;
using System.Text.RegularExpressions;
using VRAirBridge.Core;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>
/// Source-level checks for the WPF app (the test project does not reference it, so these read the .cs / .xaml files):
/// no user-visible CJK text outside the string tables, and every key the app uses exists in all four tables.
/// </summary>
public class AppLocalizationTests
{
    private static readonly Regex XamlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex XamlKey = new(@"\{l:T\s+([^\s},]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex KeyShape = new(@"^[a-z][A-Za-z0-9]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);

    [Fact]
    public void AppCSharp_HasNoCjkStringOrCharLiterals()
    {
        var hits = new List<string>();
        var files = SourceFiles("*.cs");
        Assert.True(files.Count > 20, "found only " + files.Count + " App source files under " + AppDir);
        foreach (var f in files)
        {
            foreach (var (line, text) in CSharpLiterals.Scan(File.ReadAllText(f, Encoding.UTF8)))
                if (TextUtil.HasCjk(text)) hits.Add(Path.GetFileName(f) + ":" + line + " " + text);
        }
        Assert.True(hits.Count == 0, "CJK literals in the app: " + string.Join(" | ", hits.Take(15)));
    }

    [Fact]
    public void AppXaml_HasNoCjkText()
    {
        var hits = new List<string>();
        var files = SourceFiles("*.xaml");
        Assert.True(files.Count > 5, "found only " + files.Count + " XAML files under " + AppDir);
        foreach (var f in files)
        {
            var lines = XamlComment.Replace(File.ReadAllText(f, Encoding.UTF8), "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (TextUtil.HasCjk(lines[i])) hits.Add(Path.GetFileName(f) + ":" + (i + 1) + " " + lines[i].Trim());
        }
        Assert.True(hits.Count == 0, "CJK text in XAML (use {l:T key}): " + string.Join(" | ", hits.Take(15)));
    }

    [Fact]
    public void XamlKeys_ExistInEveryTable()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles("*.xaml"))
            foreach (Match m in XamlKey.Matches(XamlComment.Replace(File.ReadAllText(f, Encoding.UTF8), "")))
                keys.Add(m.Groups[1].Value);
        Assert.True(keys.Count > 60, "found only " + keys.Count + " {l:T} keys");
        AssertKeysEverywhere(keys);
    }

    [Fact]
    public void CSharpKeys_ExistInEveryTable()
    {
        // Every string literal in the app that looks like a key and starts with a known key prefix
        // (Loc.T("ui.x"), Loc.T(ok ? "common.yes" : "common.no"), key tables, ...).
        var prefixes = new HashSet<string>(Loc.Table(Loc.English).Keys.Select(k => k[..k.IndexOf('.')]), StringComparer.Ordinal);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles("*.cs"))
        {
            foreach (var (_, text) in CSharpLiterals.Scan(File.ReadAllText(f, Encoding.UTF8)))
            {
                if (!KeyShape.IsMatch(text)) continue;
                if (prefixes.Contains(text[..text.IndexOf('.')])) keys.Add(text);
            }
        }
        Assert.True(keys.Count > 150, "found only " + keys.Count + " keys in the app's C# source");
        AssertKeysEverywhere(keys);
    }

    [Theory]
    [InlineData("en", "VR Air Bridge Hotspot")]
    [InlineData("zh-CN", "VR Air Bridge 热点")]
    [InlineData("ja", "VR Air Bridge ホットスポット")]
    [InlineData("ko", "VR Air Bridge 핫스팟")]
    public void ProductName_IsLocalized(string code, string expected) =>
        Assert.Equal(expected, Loc.Table(code)["app.name"]);

    [Theory]
    [InlineData("en", "hotspot", "headset", "driver", "channel")]
    [InlineData("zh-CN", "热点", "头显", "驱动", "信道")]
    [InlineData("ja", "ホットスポット", "ヘッドセット", "ドライバー", "チャンネル")]
    [InlineData("ko", "핫스팟", "헤드셋", "드라이버", "채널")]
    public void Glossary_TermsAppearInTheTable(string code, params string[] terms)
    {
        var all = string.Join("\n", Loc.Table(code).Values);
        foreach (var term in terms)
            Assert.True(all.Contains(term, StringComparison.OrdinalIgnoreCase), code + " table never uses the glossary term " + term);
    }

    private static void AssertKeysEverywhere(IEnumerable<string> keys)
    {
        var missing = new List<string>();
        foreach (var k in keys)
            foreach (var code in Loc.Codes)
                if (!Loc.HasKey(code, k)) missing.Add(code + ":" + k);
        Assert.True(missing.Count == 0, missing.Count + " keys used by the app are missing: " + string.Join(", ", missing.Take(25)));
    }

    private static readonly Lazy<string> AppDirLazy = new(FindAppDir);
    private static string AppDir => AppDirLazy.Value;

    private static List<string> SourceFiles(string pattern)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep))
            .ToList();
    }

    private static string FindAppDir() => SourceTree.Find("VRAirBridge.App");
}

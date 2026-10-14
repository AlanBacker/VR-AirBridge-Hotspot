using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VRAirBridge.Core;
using VRAirBridge.Core.Settings;
using Xunit;

namespace VRAirBridge.Tests;

public class LocalizationTableTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    public static IEnumerable<object[]> NonEnglish() => Loc.Codes.Where(c => c != Loc.English).Select(c => new object[] { c });
    public static IEnumerable<object[]> AllCodes() => Loc.Codes.Select(c => new object[] { c });

    internal static SortedSet<string> Placeholders(string s)
    {
        // 先去掉转义的 {{ }} 再找 {n}
        var t = s.Replace("{{", "").Replace("}}", "");
        return new SortedSet<string>(Placeholder.Matches(t).Select(m => m.Groups[1].Value));
    }

    [Fact]
    public void AllFourTables_AreEmbedded()
    {
        var names = Loc.ResourceNames().ToList();
        foreach (var code in Loc.Codes)
            Assert.Contains("VRAirBridge.Core.Strings." + code + ".json", names);
        Assert.Equal(new[] { "en", "zh-CN", "ja", "ko" }, Loc.Codes);
    }

    [Fact]
    public void EnglishTable_IsLarge_AndHasNoCjk()
    {
        var en = Loc.Table(Loc.English);
        Assert.True(en.Count > 600, "en table has only " + en.Count + " keys");
        foreach (var (k, v) in en)
            Assert.False(TextUtil.HasCjk(v), "en." + k + " contains CJK: " + v);
    }

    [Theory]
    [MemberData(nameof(NonEnglish))]
    public void EveryKey_ExistsInEveryTable(string code)
    {
        var en = Loc.Table(Loc.English);
        var t = Loc.Table(code);
        var missing = en.Keys.Where(k => !t.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = t.Keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, code + " is missing " + missing.Count + " keys: " + string.Join(", ", missing.Take(20)));
        Assert.True(extra.Count == 0, code + " has " + extra.Count + " keys not in en: " + string.Join(", ", extra.Take(20)));
    }

    [Theory]
    [MemberData(nameof(NonEnglish))]
    public void Placeholders_MatchEnglish(string code)
    {
        var en = Loc.Table(Loc.English);
        var bad = new List<string>();
        foreach (var (k, v) in Loc.Table(code))
        {
            if (!en.TryGetValue(k, out var e)) continue;
            if (!Placeholders(v).SetEquals(Placeholders(e))) bad.Add(k);
        }
        Assert.True(bad.Count == 0, code + " placeholder mismatch: " + string.Join(", ", bad.Take(20)));
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Templates_FormatWithoutError_AndAreNotEmpty(string code)
    {
        foreach (var (k, v) in Loc.Table(code))
        {
            Assert.False(string.IsNullOrEmpty(v), code + "." + k + " is empty");
            var ph = Placeholders(v);
            if (ph.Count == 0)
            {
                // 无参数的模板不会经过 string.Format，所以不能带 {{ }} 转义
                Assert.False(v.Contains("{{") || v.Contains("}}"), code + "." + k + " has escaped braces but no placeholders");
                continue;
            }
            var n = ph.Max(int.Parse) + 1;
            var args = Enumerable.Range(0, n).Select(i => (object)("A" + i)).ToArray();
            string s;
            try { s = string.Format(CultureInfo.InvariantCulture, v, args); }
            catch (FormatException ex) { throw new Xunit.Sdk.XunitException(code + "." + k + ": " + ex.Message); }
            Assert.DoesNotContain("{0", s);
        }
    }

    [Theory]
    [MemberData(nameof(NonEnglish))]
    public void Translations_AreActuallyTranslated(string code)
    {
        // 抽查：本地化后的表里绝大多数值应当含本语言文字（防止整张表还是英文）
        var t = Loc.Table(code);
        var withCjk = t.Values.Count(TextUtil.HasCjk);
        Assert.True(withCjk > t.Count * 0.6, code + ": only " + withCjk + "/" + t.Count + " values contain CJK text");
    }

    [Fact]
    public void Lookup_FallsBackToEnglish_ThenKey()
    {
        Assert.Equal(Loc.Lookup(Loc.English, "settings.notes"), Loc.Lookup("xx", "settings.notes"));
        Assert.Equal("no.such.key", Loc.Lookup(Loc.Japanese, "no.such.key"));
        Assert.Equal("no.such.key", Loc.Format(Loc.Korean, "no.such.key", 1, 2).Split(' ')[0]);
    }

    [Fact]
    public void NativeNames_AreInTheirOwnScript()
    {
        Assert.Equal("English", Loc.NativeName(Loc.English));
        Assert.Equal("简体中文", Loc.NativeName(Loc.ChineseSimplified));
        Assert.Equal("日本語", Loc.NativeName(Loc.Japanese));
        Assert.Equal("한국어", Loc.NativeName(Loc.Korean));
    }

    [Fact]
    public void Format_UsesRequestedLanguage_WithoutChangingCurrent()
    {
        var before = Loc.Current;
        var en = Loc.Format(Loc.English, "settings.hotspotNameTooLongMax", 32);
        var zh = Loc.Format(Loc.ChineseSimplified, "settings.hotspotNameTooLongMax", 32);
        Assert.Contains("32", en);
        Assert.Contains("32", zh);
        Assert.NotEqual(en, zh);
        Assert.False(TextUtil.HasCjk(en));
        Assert.True(TextUtil.HasCjk(zh));
        Assert.Equal(before, Loc.Current);
    }

    [Fact]
    public void MissingArguments_DoNotThrow()
    {
        var s = Loc.Format(Loc.English, "settings.hotspotNameTooLongMax", Array.Empty<object>());
        Assert.False(string.IsNullOrEmpty(s));
    }
}

public class LanguageResolutionTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("EN-gb", "en")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh_TW", "zh-CN")]
    [InlineData("ja", "ja")]
    [InlineData("ja-JP", "ja")]
    [InlineData("KO", "ko")]
    [InlineData("ko-KR", "ko")]
    [InlineData("fr-FR", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_MapsByLanguagePrefix(string? input, string? expected) => Assert.Equal(expected, Loc.Normalize(input));

    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("fr-FR", "en")]
    public void DefaultLanguage_FollowsWindowsUiCulture(string culture, string expected) =>
        Assert.Equal(expected, Loc.ResolveDefault(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void ResolveSetting_AutoAndUnknown_UseDefault()
    {
        var def = Loc.ResolveDefault();
        Assert.Equal(def, Loc.ResolveSetting("auto"));
        Assert.Equal(def, Loc.ResolveSetting("AUTO"));
        Assert.Equal(def, Loc.ResolveSetting(null));
        Assert.Equal(def, Loc.ResolveSetting(" "));
        Assert.Equal(def, Loc.ResolveSetting("xx-YY"));
        Assert.Equal("ja", Loc.ResolveSetting("ja"));
        Assert.Equal("ko", Loc.ResolveSetting("ko-KR"));
    }

    [Fact]
    public void CultureFor_ReturnsMatchingCulture()
    {
        Assert.Equal("zh-CN", Loc.CultureFor(Loc.ChineseSimplified).Name);
        Assert.Equal("ja", Loc.CultureFor(Loc.Japanese).Name);
        Assert.Equal("ko", Loc.CultureFor(Loc.Korean).Name);
        Assert.Equal("en", Loc.CultureFor(Loc.English).Name);
    }
}

[Collection(LanguageSwitchCollection.Name)]
public class LanguageSwitchTests
{
    [Fact]
    public void SetLanguage_RaisesChanged_AndCoreMessagesFollow()
    {
        var raised = 0;
        void OnChanged() => raised++;
        Loc.LanguageChanged += OnChanged;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("en", Loc.Current);
            Assert.Equal(1, raised);
            var en = AppSettings.Validate("", "12345678");
            Assert.NotNull(en);
            Assert.False(TextUtil.HasCjk(en!));

            Loc.SetLanguage("en-US"); // 同一种语言不再触发
            Assert.Equal(1, raised);

            Loc.SetLanguage("ko");
            Assert.Equal(2, raised);
            var ko = AppSettings.Validate("", "12345678");
            Assert.NotNull(ko);
            Assert.Matches("[가-힯]", ko!);

            Loc.SetLanguage("ja");
            var ja = AppSettings.Validate("ok", "short");
            Assert.NotNull(ja);
            Assert.Matches("[぀-ヿ]", ja!); // 假名
        }
        finally
        {
            Loc.LanguageChanged -= OnChanged;
            Loc.SetLanguage(Loc.ChineseSimplified);
        }
        Assert.Equal(Loc.ChineseSimplified, Loc.Current);
    }
}

public class UiSettingsTests
{
    [Fact]
    public void UiSettings_DefaultToAuto_AndPersist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrab-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            var s = store.Load();
            Assert.Equal("auto", s.Ui.Language);
            Assert.Equal("system", s.Ui.Theme);
            s.Ui.Language = "ja";
            s.Ui.Theme = "dark";
            store.Save(s);
            var json = File.ReadAllText(store.ConfigPath);
            Assert.Contains("\"ui\"", json);
            Assert.Contains("\"language\": \"ja\"", json);
            var s2 = new SettingsStore(dir).Load();
            Assert.Equal("ja", s2.Ui.Language);
            Assert.Equal("dark", s2.Ui.Theme);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void OldConfig_WithoutUiSection_GetsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrab-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ \"ssid\": \"Test\", \"band\": \"5GHz\", \"ui\": null }");
            var s = new SettingsStore(dir).Load();
            Assert.Equal("auto", s.Ui.Language);
            Assert.Equal("system", s.Ui.Theme);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ \"ui\": { \"language\": \"\" } }");
            s = new SettingsStore(dir).Load();
            Assert.Equal("auto", s.Ui.Language);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class NoCjkInCoreSourceTests
{
    [Fact]
    public void CoreSource_HasNoCjkStringOrCharLiterals()
    {
        var core = FindCoreDir();
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep))
            .ToList();
        Assert.True(files.Count > 20, "found only " + files.Count + " Core source files under " + core);
        var hits = new List<string>();
        foreach (var f in files)
        {
            var src = File.ReadAllText(f, Encoding.UTF8);
            foreach (var (line, text) in CSharpLiterals.Scan(src))
                if (TextUtil.HasCjk(text)) hits.Add(Path.GetFileName(f) + ":" + line + " " + text);
        }
        Assert.True(hits.Count == 0, "CJK literals in Core: " + string.Join(" | ", hits.Take(15)));
    }

    [Fact]
    public void Scanner_FindsLiterals_AndSkipsComments()
    {
        const string src = "// 注释\nvar a = \"中\"; /* 注释 */ var b = '文'; var q = '\\''; var c = @\"x\"\"一\"; var d = $\"{x} 高\";";
        var lits = CSharpLiterals.Scan(src).Select(l => l.Text).ToList();
        Assert.Equal(4, lits.Count(TextUtil.HasCjk));
        Assert.Equal(5, lits.Count);
        Assert.DoesNotContain(lits, l => l.Contains("注释"));
    }

    private static string FindCoreDir() => SourceTree.Find("VRAirBridge.Core");
}

internal static class TextUtil
{
    /// <summary>U+3000–30FF、U+3400–4DBF、U+4E00–9FFF、U+AC00–D7AF、U+FF00–FFEF。</summary>
    public static bool HasCjk(string s)
    {
        foreach (var ch in s)
        {
            if ((ch >= '　' && ch <= 'ヿ') || (ch >= '㐀' && ch <= '䶿') || (ch >= '一' && ch <= '鿿')
                || (ch >= '가' && ch <= '힯') || (ch >= '＀' && ch <= '￯')) return true;
        }
        return false;
    }
}

/// <summary>极简 C# 词法扫描：跳过注释，取出字符串（普通 / 逐字 / 插值 / 原始）和字符字面量的内容。</summary>
internal static class CSharpLiterals
{
    public static IEnumerable<(int Line, string Text)> Scan(string s)
    {
        var res = new List<(int, string)>();
        int i = 0, n = s.Length, line = 1, lineScanned = 0;
        int LineAt(int p)
        {
            for (; lineScanned < p && lineScanned < n; lineScanned++) if (s[lineScanned] == '\n') line++;
            return line;
        }
        while (i < n)
        {
            var c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2; continue; }
            if (c == '\'')
            {
                var st = i++;
                while (i < n && s[i] != '\'') { if (s[i] == '\\') i++; i++; }
                var end = Math.Min(i, n);
                res.Add((LineAt(st), s.Substring(st + 1, Math.Max(0, end - st - 1))));
                i++;
                continue;
            }
            var isStr = c == '"'
                || ((c == '$' || c == '@') && i + 1 < n && (s[i + 1] == '"' || ((s[i + 1] == '$' || s[i + 1] == '@') && i + 2 < n && s[i + 2] == '"')));
            if (isStr)
            {
                var st = i;
                var verbatim = false;
                while (s[i] != '"') { if (s[i] == '@') verbatim = true; i++; }
                // 原始字符串 """..."""
                var q = 0;
                while (i + q < n && s[i + q] == '"') q++;
                if (q >= 3)
                {
                    var close = new string('"', q);
                    var e = s.IndexOf(close, i + q, StringComparison.Ordinal);
                    e = e < 0 ? n : e;
                    res.Add((LineAt(st), s.Substring(i + q, e - i - q)));
                    i = Math.Min(n, e + q);
                    continue;
                }
                i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (s[i] == '"')
                    {
                        if (verbatim && i + 1 < n && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (!verbatim && s[i] == '\\' && i + 1 < n) { sb.Append(s[i]).Append(s[i + 1]); i += 2; continue; }
                    sb.Append(s[i]);
                    i++;
                }
                res.Add((LineAt(st), sb.ToString()));
                continue;
            }
            i++;
        }
        return res;
    }
}

using System.Globalization;
using System.Text.Json;

namespace VRAirBridge.Core;

/// <summary>
/// 界面语言服务：所有面向用户的文字都按 key 从内嵌的 JSON 字符串表里取（Localization/Strings/&lt;code&gt;.json）。
/// 查找顺序：当前语言 → en → zh-CN → key 本身。切换语言时触发 <see cref="LanguageChanged"/>。
/// 文字在生成时就按当前语言格式化；已经写进日志的行保持原来的语言。
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-CN";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string Auto = "auto";

    /// <summary>支持的语言代码，按切换器里的显示顺序。</summary>
    public static IReadOnlyList<string> Codes { get; } = new[] { English, ChineseSimplified, Japanese, Korean };

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Tables = new(LoadTables, LazyThreadSafetyMode.ExecutionAndPublication);
    private static volatile string _current = ResolveDefault();

    /// <summary>语言切换后触发（可能在任意线程上）。</summary>
    public static event Action? LanguageChanged;

    public static string Current => _current;

    /// <summary>当前语言对应的区域性（用于数字 / 日期格式）。</summary>
    public static CultureInfo Culture => CultureFor(_current);

    public static void SetLanguage(string? code)
    {
        var c = ResolveSetting(code);
        if (c == _current) return;
        _current = c;
        try { LanguageChanged?.Invoke(); } catch { }
    }

    /// <summary>设置值（auto / en / zh-CN / ja / ko，大小写和地区后缀都容忍）→ 实际语言代码。</summary>
    public static string ResolveSetting(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Trim().Equals(Auto, StringComparison.OrdinalIgnoreCase))
            return ResolveDefault();
        return Normalize(setting) ?? ResolveDefault();
    }

    /// <summary>按 Windows 显示语言选默认语言：中文 → 简体中文，日语，韩语，其它 → English。</summary>
    public static string ResolveDefault(CultureInfo? uiCulture = null)
    {
        var c = uiCulture ?? CultureInfo.CurrentUICulture;
        return Normalize(c.Name) ?? Normalize(c.TwoLetterISOLanguageName) ?? English;
    }

    /// <summary>把 "zh-Hans-CN"、"ja-JP"、"KO" 之类规范成支持的代码；不认识返回 null。</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().Replace('_', '-').ToLowerInvariant();
        var lang = c.Split('-')[0];
        return lang switch
        {
            "en" => English,
            "zh" => ChineseSimplified,
            "ja" => Japanese,
            "ko" => Korean,
            _ => null,
        };
    }

    public static CultureInfo CultureFor(string code)
    {
        try { return CultureInfo.GetCultureInfo(code == ChineseSimplified ? "zh-CN" : code); }
        catch { return CultureInfo.InvariantCulture; }
    }

    /// <summary>取当前语言的原始模板（不做 string.Format，模板里的 {0} 原样保留）。</summary>
    public static string T(string key) => Lookup(_current, key);

    /// <summary>取当前语言的模板并用参数格式化。</summary>
    public static string T(string key, params object?[] args) => Format(_current, key, args);

    /// <summary>按指定语言格式化（不改变当前语言，测试和诊断用）。</summary>
    public static string Format(string lang, string key, params object?[] args)
    {
        var t = Lookup(lang, key);
        if (args == null || args.Length == 0) return t;
        try { return string.Format(CultureFor(lang), t, args); }
        catch (FormatException) { return t + " " + string.Join(" ", args.Select(a => a?.ToString())); }
    }

    /// <summary>当前语言 → en → zh-CN → key。</summary>
    public static string Lookup(string lang, string key)
    {
        var tables = Tables.Value;
        if (tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return v;
        if (tables.TryGetValue(English, out t) && t.TryGetValue(key, out v)) return v;
        if (tables.TryGetValue(ChineseSimplified, out t) && t.TryGetValue(key, out v)) return v;
        return key;
    }

    public static bool HasKey(string lang, string key) => Tables.Value.TryGetValue(lang, out var t) && t.ContainsKey(key);

    /// <summary>某种语言的完整字符串表（只读副本，测试用）。</summary>
    public static IReadOnlyDictionary<string, string> Table(string lang) =>
        Tables.Value.TryGetValue(lang, out var t) ? t : new Dictionary<string, string>();

    /// <summary>语言自己的名字（English / 简体中文 / 日本語 / 한국어），用于切换器。</summary>
    public static string NativeName(string code) => Lookup(code, "lang.nativeName");

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var asm = typeof(Loc).Assembly;
        foreach (var code in Codes)
        {
            var name = "VRAirBridge.Core.Strings." + code + ".json";
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) { res[code] = new Dictionary<string, string>(); continue; }
            res[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        return res;
    }

    /// <summary>内嵌字符串表的资源名（测试用）。</summary>
    public static IEnumerable<string> ResourceNames() =>
        typeof(Loc).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("VRAirBridge.Core.Strings.", StringComparison.Ordinal));
}

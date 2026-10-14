using System.Text;

namespace VRAirBridge.Core;

/// <summary>
/// Display-only text shaping for wrapping TextBlocks (never for editable text, passwords or automation names):
/// <list type="bullet">
/// <item>Korean: WPF breaks Hangul between any two syllables. A WORD JOINER (U+2060, zero width) between adjacent
/// non-space characters that involve Hangul makes lines break only at spaces, like CSS <c>word-break: keep-all</c>.
/// Joiners go only where a line could actually break: no CJK font has U+2060, so WPF lays each one out as a separate
/// font run, and thousands of them made wrapping text slow to lay out while the window was resized.</item>
/// <item>Hyphens inside a token ("D-Link", "WPA2-Personal") become NON-BREAKING HYPHENS (U+2011), so a line never ends
/// in "D-" with "Link" below.</item>
/// <item>A short bracketed group ("(1 total)", "(best -48 dBm)") keeps its spaces unbreakable, so it is never split
/// across two lines.</item>
/// </list>
/// </summary>
public static class DisplayText
{
    public const char WordJoiner = '\u2060';
    public const char NonBreakingHyphen = '\u2011';
    public const char NoBreakSpace = '\u00A0';

    /// <summary>Longest bracket content (characters between the brackets) that is kept on one line.</summary>
    public const int ShortGroup = 16;

    public static bool IsHangul(char c) =>
        c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';

    private static bool IsBreakable(char c) => char.IsWhiteSpace(c) && c != NoBreakSpace;

    private static int CloseOf(string s, int open)
    {
        char close = s[open] == '(' ? ')' : '\uFF09';
        for (int j = open + 1; j < s.Length && j - open - 1 <= ShortGroup; j++)
        {
            char c = s[j];
            if (c == close) return j;
            if (c is '(' or '\uFF08' or '\n' or '\r') return -1;
        }
        return -1;
    }

    /// <summary>
    /// Makes <paramref name="s"/> one unbreakable unit ("channel 36/80  -48 dBm"): spaces become no-break spaces,
    /// hyphens non-breaking hyphens, and a word joiner goes wherever a line could still break (around CJK, kana and
    /// Hangul, or after a "/" between letters).
    /// </summary>
    public static string KeepTogether(string s)
    {
        s = s.Replace(' ', NoBreakSpace).Replace('-', NonBreakingHyphen);
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i + 1 < s.Length && !Holds(s, i)) sb.Append(WordJoiner);
        }
        return sb.ToString();
    }

    /// <summary>A letter or digit of an alphabetic script (no line break between two of them).</summary>
    private static bool IsPlain(char c) => c < '\u0E00' && char.IsLetterOrDigit(c);

    private static bool IsGlue(char c) => c is NoBreakSpace or NonBreakingHyphen or WordJoiner or '\u202F';

    /// <summary>
    /// True when a line never breaks between the non-space characters <c>s[i]</c> and <c>s[i + 1]</c> anyway
    /// (Unicode line breaking as WPF applies it), so no word joiner is needed there.
    /// </summary>
    private static bool Holds(string s, int i)
    {
        char a = s[i], b = s[i + 1];
        if (IsGlue(a) || IsGlue(b) || IsPlain(a) && IsPlain(b)) return true;
        // never before closing / infix punctuation, never after an opening bracket
        if (b is ')' or ']' or '}' or ',' or '.' or ':' or ';' or '!' or '?' or '/' or '%'
              or '\uFF09' or '\uFF0C' or '\u3001' or '\u3002' or '\uFF1A' or '\uFF1B' or '\uFF01' or '\uFF1F') return true;
        if (a is '(' or '[' or '{' or '\uFF08') return true;
        // "1.5", "a,b", "x:y", "36/80" (but "a/b" may break after the slash)
        if (a is '.' or ',' or ':') return IsPlain(b);
        return a == '/' && i > 0 && char.IsAsciiDigit(s[i - 1]) && char.IsAsciiDigit(b);
    }

    public static string Shape(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2) return s ?? "";

        // pass 1: spaces inside short bracketed groups become no-break spaces
        char[]? glued = null;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is not ('(' or '\uFF08')) continue;
            int close = CloseOf(s, i);
            if (close < 0) continue;
            for (int j = i + 1; j < close; j++)
                if (s[j] == ' ') (glued ??= s.ToCharArray())[j] = NoBreakSpace;
            i = close;
        }
        if (glued != null) s = new string(glued);

        // pass 2: hyphens inside a token become non-breaking hyphens
        char[]? hy = null;
        for (int i = 1; i + 1 < s.Length; i++)
            if (s[i] == '-' && !IsBreakable(s[i - 1]) && !IsBreakable(s[i + 1])) (hy ??= s.ToCharArray())[i] = NonBreakingHyphen;
        if (hy != null) s = new string(hy);

        // pass 3: Korean keep-all
        StringBuilder? sb = null;
        int done = 0;
        for (int i = 0; i + 1 < s.Length; i++)
        {
            char c = s[i], next = s[i + 1];
            if (IsBreakable(c) || IsBreakable(next) || !(IsHangul(c) || IsHangul(next)) || Holds(s, i)) continue;
            (sb ??= new StringBuilder(s.Length + 16)).Append(s, done, i + 1 - done).Append(WordJoiner);
            done = i + 1;
        }
        return sb == null ? s : sb.Append(s, done, s.Length - done).ToString();
    }
}

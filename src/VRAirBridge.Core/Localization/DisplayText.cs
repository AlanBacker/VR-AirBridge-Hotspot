using System.Text;

namespace VRAirBridge.Core;

/// <summary>
/// Display-only text shaping for wrapping TextBlocks (never for editable text, passwords or automation names):
/// <list type="bullet">
/// <item>Korean: WPF breaks Hangul between any two syllables. A WORD JOINER (U+2060, zero width) between every pair of
/// adjacent non-space characters that involves Hangul makes lines break only at spaces, like CSS <c>word-break: keep-all</c>.</item>
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

        // pass 2: non-breaking hyphens, Korean keep-all
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool prevSolid = i > 0 && !IsBreakable(s[i - 1]);
            bool nextSolid = i + 1 < s.Length && !IsBreakable(s[i + 1]);
            char outC = c == '-' && prevSolid && nextSolid ? NonBreakingHyphen : c;
            bool join = nextSolid && !IsBreakable(c) && c != WordJoiner && s[i + 1] != WordJoiner &&
                        (IsHangul(c) || IsHangul(s[i + 1]));
            if (sb == null && (outC != c || join))
            {
                sb = new StringBuilder(s.Length + 16);
                sb.Append(s, 0, i);
            }
            if (sb == null) continue;
            sb.Append(outC);
            if (join) sb.Append(WordJoiner);
        }
        return sb?.ToString() ?? s;
    }
}

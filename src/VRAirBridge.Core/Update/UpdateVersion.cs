using System.Globalization;

namespace VRAirBridge.Core.Update;

/// <summary>
/// Release version "1.2.3" (optionally "v1.2.3", "1.2", "1.2.3.4", "1.2.3-beta.2"). Compared like SemVer: numbers
/// first, then a release beats any pre-release of the same numbers; pre-release parts compare numerically when both
/// are numbers, otherwise ordinally.
/// </summary>
public readonly record struct UpdateVersion(int[] Numbers, string[] PreRelease) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion v)
    {
        v = default;
        var t = AppInfo.StripMetadata(text);
        if (t.Length == 0) return false;
        string core = t, pre = "";
        int dash = t.IndexOf('-');
        if (dash >= 0)
        {
            core = t[..dash];
            pre = t[(dash + 1)..];
            if (pre.Length == 0) return false; // "1.2-"
        }
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var nums = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        var preParts = pre.Length == 0 ? Array.Empty<string>() : pre.Split('.');
        if (preParts.Any(p => p.Length == 0)) return false;
        v = new UpdateVersion(nums, preParts);
        return true;
    }

    public bool IsPreRelease => PreRelease is { Length: > 0 };

    public int CompareTo(UpdateVersion other)
    {
        var a = Numbers ?? new int[4];
        var b = other.Numbers ?? new int[4];
        for (int i = 0; i < 4; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        var pa = PreRelease ?? Array.Empty<string>();
        var pb = other.PreRelease ?? Array.Empty<string>();
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length); // release > pre-release
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            bool na = int.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ia);
            bool nb = int.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ib);
            int c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return Math.Sign(c);
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>True when the candidate is a valid version newer than the current one.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var c)) return false;
        if (!TryParse(current, out var cur)) return true;
        return c.CompareTo(cur) > 0;
    }

    /// <summary>Same version (ignoring "v" and "+metadata").</summary>
    public static bool Same(string? a, string? b) =>
        TryParse(a, out var x) && TryParse(b, out var y) && x.CompareTo(y) == 0;

    public override string ToString()
    {
        var n = Numbers ?? new int[4];
        var s = string.Join(".", n.Take(n[3] != 0 ? 4 : 3));
        return PreRelease is { Length: > 0 } ? s + "-" + string.Join(".", PreRelease) : s;
    }
}

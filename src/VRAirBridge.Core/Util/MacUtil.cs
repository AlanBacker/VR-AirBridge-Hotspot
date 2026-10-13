using System.Net.NetworkInformation;

namespace VRAirBridge.Core.Util;

public static class MacUtil
{
    /// <summary>把各种格式的 MAC（BC2228C86D36 / bc:22:28:… / BC-22-…）统一成 12 位大写十六进制；不合法返回空串。</summary>
    public static string Normalize(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";
        Span<char> buf = stackalloc char[mac.Length];
        int n = 0;
        foreach (var c in mac)
            if (Uri.IsHexDigit(c)) buf[n++] = char.ToUpperInvariant(c);
        return n == 12 ? new string(buf[..n]) : "";
    }

    public static string Format(string? mac)
    {
        var m = Normalize(mac);
        if (m.Length != 12) return mac ?? "";
        return string.Join("-", Enumerable.Range(0, 6).Select(i => m.Substring(i * 2, 2)));
    }

    public static string Format(byte[] mac, int len = 6)
    {
        if (mac.Length < len) len = mac.Length;
        return string.Join("-", mac.Take(len).Select(b => b.ToString("X2")));
    }

    public static string Format(PhysicalAddress? pa) => pa == null ? "" : Format(pa.GetAddressBytes());

    /// <summary>
    /// Wi-Fi Direct 虚拟网卡（热点 GO）的 MAC = 物理网卡 MAC 只改第一个字节；
    /// 比较后 5 个字节即可判断热点是否开在这块物理网卡上。
    /// </summary>
    public static bool SameRadio(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length != 12 || y.Length != 12) return false;
        return string.Equals(x[2..], y[2..], StringComparison.Ordinal);
    }

    public static string Last4(string? mac)
    {
        var m = Normalize(mac);
        return m.Length == 12 ? m[8..] : "";
    }
}

using System.Text;
using QRCoder;

namespace VRAirBridge.Core.Qr;

/// <summary>A QR code as a square module matrix (true = dark), without the quiet zone.</summary>
public sealed class QrMatrix
{
    public QrMatrix(bool[,] modules) => Modules = modules;

    /// <summary>[row, column]; true = dark module.</summary>
    public bool[,] Modules { get; }

    /// <summary>Number of modules per side (21 for version 1, +4 per version).</summary>
    public int Size => Modules.GetLength(0);

    /// <summary>QR version (1..40).</summary>
    public int Version => (Size - 17) / 4;

    /// <summary>Recommended quiet zone around the code, in modules (draw it in the background colour).</summary>
    public const int QuietZone = 4;

    public bool this[int row, int col] => Modules[row, col];
}

/// <summary>
/// "Join Wi-Fi" QR codes (the ZXing WIFI: format that the iPhone / Android camera understands).
/// The payload contains the hotspot passphrase in clear text: never log it, never write it to disk,
/// and in screenshot / mock mode build it from the mock password only.
/// </summary>
public static class WifiQr
{
    /// <summary>
    /// "WIFI:T:WPA;S:&lt;ssid&gt;;P:&lt;passphrase&gt;;;". WPA2, WPA3 transition and WPA3 all use T:WPA
    /// (the phone negotiates WPA2 / WPA3 itself); an empty passphrase gives T:nopass.
    /// Special characters \ ; , : " are backslash-escaped.
    /// </summary>
    public static string BuildPayload(string ssid, string? passphrase, string? auth = null)
    {
        ArgumentNullException.ThrowIfNull(ssid);
        var sb = new StringBuilder(32 + ssid.Length * 2 + (passphrase?.Length ?? 0) * 2);
        sb.Append("WIFI:");
        if (string.IsNullOrEmpty(passphrase))
        {
            sb.Append("T:nopass;S:").Append(Escape(ssid)).Append(";;");
            return sb.ToString();
        }
        sb.Append("T:").Append(AuthType(auth)).Append(";S:").Append(Escape(ssid)).Append(";P:").Append(Escape(passphrase)).Append(";;");
        return sb.ToString();
    }

    /// <summary>Maps the settings value (WPA2 / WPA3Transition / WPA3) to the WIFI: T field.</summary>
    public static string AuthType(string? auth) => auth?.Trim().ToUpperInvariant() switch
    {
        "WEP" => "WEP",
        "NONE" or "OPEN" or "NOPASS" => "nopass",
        _ => "WPA",
    };

    /// <summary>Backslash-escapes \ ; , : and " (ZXing WIFI: format).</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Encodes any text (UTF-8, byte mode, error correction M) as a QR matrix without the quiet zone.</summary>
    public static QrMatrix Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: true, utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Default);
        var rows = data.ModuleMatrix;
        int full = rows.Count;
        // QRCoder includes a 4-module quiet zone on each side
        int border = 0;
        while (border < full / 2 && !RowHasDark(rows, border) && !ColHasDark(rows, border)) border++;
        int size = full - 2 * border;
        var m = new bool[size, size];
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                m[r, c] = rows[r + border][c + border];
        return new QrMatrix(m);
    }

    /// <summary>BuildPayload + Encode. The returned matrix encodes the passphrase: do not persist it.</summary>
    public static QrMatrix BuildMatrix(string ssid, string? passphrase, string? auth = null) => Encode(BuildPayload(ssid, passphrase, auth));

    private static bool RowHasDark(List<System.Collections.BitArray> rows, int r)
    {
        var row = rows[r];
        for (int c = 0; c < row.Length; c++) if (row[c]) return true;
        return false;
    }

    private static bool ColHasDark(List<System.Collections.BitArray> rows, int c)
    {
        foreach (var row in rows) if (c < row.Length && row[c]) return true;
        return false;
    }
}

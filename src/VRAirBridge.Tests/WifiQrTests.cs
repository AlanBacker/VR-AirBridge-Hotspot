using VRAirBridge.Core.Qr;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace VRAirBridge.Tests;

/// <summary>"Join by QR" payload and matrix. Demo values only, never a real hotspot password.</summary>
public class WifiQrTests
{
    [Fact]
    public void Payload_Wpa()
    {
        Assert.Equal("WIFI:T:WPA;S:VR-AirBridge-Demo;P:demo-pass-1234;;", WifiQr.BuildPayload("VR-AirBridge-Demo", "demo-pass-1234", "WPA2"));
        // WPA3 and the transition mode use the same T:WPA (the phone negotiates)
        Assert.Equal(WifiQr.BuildPayload("a", "b", "WPA2"), WifiQr.BuildPayload("a", "b", "WPA3"));
        Assert.Equal(WifiQr.BuildPayload("a", "b", "WPA2"), WifiQr.BuildPayload("a", "b", "WPA3Transition"));
        Assert.Equal(WifiQr.BuildPayload("a", "b", "WPA2"), WifiQr.BuildPayload("a", "b"));
    }

    [Fact]
    public void Payload_Open()
    {
        Assert.Equal("WIFI:T:nopass;S:Open Net;;", WifiQr.BuildPayload("Open Net", ""));
        Assert.Equal("WIFI:T:nopass;S:Open Net;;", WifiQr.BuildPayload("Open Net", null, "WPA2"));
    }

    [Fact]
    public void Payload_EscapesSpecialCharacters()
    {
        Assert.Equal("a\\\\b\\;c\\,d\\:e\\\"f g", WifiQr.Escape("a\\b;c,d:e\"f g"));
        Assert.Equal("WIFI:T:WPA;S:Lab\\;5G;P:x\\:y\\,z;;", WifiQr.BuildPayload("Lab;5G", "x:y,z"));
        Assert.Throws<ArgumentNullException>(() => WifiQr.BuildPayload(null!, "x"));
    }

    [Theory]
    [InlineData("WPA2", "WPA")]
    [InlineData("wpa3transition", "WPA")]
    [InlineData(null, "WPA")]
    [InlineData("WEP", "WEP")]
    [InlineData(" open ", "nopass")]
    [InlineData("None", "nopass")]
    public void AuthType(string? auth, string expected) => Assert.Equal(expected, WifiQr.AuthType(auth));

    [Fact]
    public void Matrix_HasFinderPatterns_AndNoQuietZone()
    {
        var m = WifiQr.BuildMatrix("VR-AirBridge-Demo", "demo-pass-1234");
        int n = m.Size;
        Assert.Equal(17 + 4 * m.Version, n);
        Assert.InRange(m.Version, 1, 40);
        // the three 7x7 finder patterns start right at the edges: dark ring, light ring, dark centre
        foreach (var (r, c) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
        {
            Assert.True(m[r, c]);
            Assert.True(m[r + 6, c + 6]);
            Assert.False(m[r + 1, c + 1]);
            Assert.True(m[r + 3, c + 3]);
        }
        // separators next to the finder patterns are light
        Assert.False(m[7, 7]);
    }

    [Theory]
    [InlineData("VR-AirBridge-Demo", "demo-pass-1234", "WPA2")]
    [InlineData("Lab;5G \"x\"", "a:b,c;d\\e", "WPA3Transition")]
    [InlineData("热点 VR-测试", "演示密码-demo", "WPA2")]
    [InlineData("Open Net", "", null)]
    public void Matrix_DecodesBackToThePayload(string ssid, string pass, string? auth)
    {
        var payload = WifiQr.BuildPayload(ssid, pass, auth);
        Assert.Equal(payload, Decode(WifiQr.Encode(payload)));
    }

    /// <summary>Renders the matrix with the quiet zone (4 px per module) and decodes it with ZXing.</summary>
    private static string Decode(QrMatrix m)
    {
        const int px = 4;
        int side = (m.Size + 2 * QrMatrix.QuietZone) * px;
        var gray = new byte[side * side];
        Array.Fill(gray, (byte)255);
        for (int r = 0; r < m.Size; r++)
            for (int c = 0; c < m.Size; c++)
            {
                if (!m[r, c]) continue;
                for (int y = 0; y < px; y++)
                    for (int x = 0; x < px; x++)
                        gray[((r + QrMatrix.QuietZone) * px + y) * side + (c + QrMatrix.QuietZone) * px + x] = 0;
            }
        var bitmap = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(gray, side, side, RGBLuminanceSource.BitmapFormat.Gray8)));
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.PURE_BARCODE] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var result = new QRCodeReader().decode(bitmap, hints);
        Assert.NotNull(result);
        return result.Text;
    }
}

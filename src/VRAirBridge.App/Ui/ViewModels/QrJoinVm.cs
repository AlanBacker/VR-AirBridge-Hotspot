using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRAirBridge.Core;
using VRAirBridge.Core.Qr;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>
/// The "Join by QR" part of a dialog: the Wi-Fi QR code, the network name and the password (masked until the user
/// reveals it). The password only lives in memory here: it is never logged and never part of the dialog's copy text.
/// </summary>
public sealed class QrJoinVm : ObservableObject
{
    private readonly string _password;
    private bool _revealed;

    internal QrJoinVm(string ssid, string password, string? auth)
    {
        Ssid = ssid;
        _password = password ?? "";
        var bmp = Render(WifiQr.BuildMatrix(ssid, _password, auth));
        Image = bmp;
        // whole pixels per module at 100 % scaling, about 230 px wide
        ImageSize = bmp.PixelWidth * Math.Max(1, 232 / bmp.PixelWidth);
        ToggleCommand = new RelayCommand(() => Revealed = !Revealed);
    }

    /// <summary>One pixel per module (quiet zone included); the view scales it up without smoothing.</summary>
    public ImageSource Image { get; }
    public double ImageSize { get; }
    public string Ssid { get; }
    public bool HasPassword => _password.Length > 0;
    public ICommand ToggleCommand { get; }

    public bool Revealed
    {
        get => _revealed;
        private set
        {
            if (!Set(ref _revealed, value)) return;
            OnPropertyChanged(nameof(PasswordText));
            OnPropertyChanged(nameof(ToggleTip));
            OnPropertyChanged(nameof(ToggleIcon));
        }
    }

    public string PasswordText => _revealed ? _password : new string('\u2022', Math.Clamp(_password.Length, 8, 16));
    public string ToggleTip => Loc.T(_revealed ? "ui.qr.hide" : "ui.qr.show");
    public string ToggleIcon => _revealed ? "I.eye-off" : "I.eye";

    internal static BitmapSource Render(QrMatrix m)
    {
        int q = QrMatrix.QuietZone, n = m.Size + 2 * q;
        var px = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int r = y - q, c = x - q;
                bool dark = r >= 0 && c >= 0 && r < m.Size && c < m.Size && m[r, c];
                px[y * n + x] = dark ? (byte)0 : (byte)255;
            }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Gray8, null, px, n);
        bmp.Freeze();
        return bmp;
    }
}

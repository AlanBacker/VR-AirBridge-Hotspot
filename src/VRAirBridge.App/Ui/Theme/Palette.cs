using System.Windows.Media;

namespace VRAirBridge.App.Ui.Theme;

/// <summary>
/// Colour palette ported from the reference design (VRChat-DLSS5-Cam). Two fixed palettes
/// (dark / light) are blended by a factor t (0 = dark, 1 = light) while the theme animates.
/// </summary>
internal sealed class Palette
{
    public Color Accent, AccentHover, AccentActive, AccentText;
    public Color Good, Warn, Bad, Muted;
    public Color Panel, PanelBorder, OverlayBg;
    public Color Text, TextDim;
    public Color Control, ControlHover, ControlActive;
    public Color Surface, Track, RangeFill, Knob, Selection;
    public Color Card, CardBorder, Shadow, Window;

    private static Color C(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);

    public static readonly Palette Dark = new()
    {
        Accent = C(92, 108, 245), AccentHover = C(114, 129, 255), AccentActive = C(76, 91, 222), AccentText = C(255, 255, 255),
        Good = C(61, 214, 140), Warn = C(255, 184, 71), Bad = C(255, 99, 105), Muted = C(128, 134, 150),
        Panel = C(25, 28, 34), PanelBorder = C(38, 42, 51), OverlayBg = C(12, 13, 17, 208),
        Text = C(232, 234, 240), TextDim = C(150, 156, 172),
        Control = C(37, 41, 50), ControlHover = C(47, 52, 63), ControlActive = C(58, 64, 78),
        Surface = C(9, 10, 13), Track = C(52, 57, 69), RangeFill = C(92, 108, 245, 110), Knob = C(240, 242, 247),
        Selection = C(92, 108, 245, 60),
        Card = C(25, 28, 34), CardBorder = C(38, 42, 51), Shadow = C(0, 0, 0, 150), Window = C(15, 16, 20),
    };

    public static readonly Palette Light = new()
    {
        Accent = C(70, 88, 230), AccentHover = C(92, 109, 242), AccentActive = C(56, 72, 204), AccentText = C(255, 255, 255),
        Good = C(22, 156, 86), Warn = C(194, 122, 8), Bad = C(214, 55, 62), Muted = C(118, 125, 140),
        Panel = C(255, 255, 255), PanelBorder = C(223, 226, 233), OverlayBg = C(255, 255, 255, 228),
        Text = C(27, 30, 37), TextDim = C(102, 109, 125),
        Control = C(238, 240, 245), ControlHover = C(228, 231, 238), ControlActive = C(214, 218, 228),
        Surface = C(226, 229, 235), Track = C(212, 216, 225), RangeFill = C(70, 88, 230, 90), Knob = C(52, 58, 70),
        Selection = C(70, 88, 230, 46),
        Card = C(255, 255, 255), CardBorder = C(223, 226, 233), Shadow = C(22, 28, 50, 44), Window = C(241, 243, 247),
    };

    public static Palette Blend(double t)
    {
        var a = Dark;
        var b = Light;
        return new Palette
        {
            Accent = Mix(a.Accent, b.Accent, t), AccentHover = Mix(a.AccentHover, b.AccentHover, t),
            AccentActive = Mix(a.AccentActive, b.AccentActive, t), AccentText = Mix(a.AccentText, b.AccentText, t),
            Good = Mix(a.Good, b.Good, t), Warn = Mix(a.Warn, b.Warn, t), Bad = Mix(a.Bad, b.Bad, t), Muted = Mix(a.Muted, b.Muted, t),
            Panel = Mix(a.Panel, b.Panel, t), PanelBorder = Mix(a.PanelBorder, b.PanelBorder, t), OverlayBg = Mix(a.OverlayBg, b.OverlayBg, t),
            Text = Mix(a.Text, b.Text, t), TextDim = Mix(a.TextDim, b.TextDim, t),
            Control = Mix(a.Control, b.Control, t), ControlHover = Mix(a.ControlHover, b.ControlHover, t),
            ControlActive = Mix(a.ControlActive, b.ControlActive, t),
            Surface = Mix(a.Surface, b.Surface, t), Track = Mix(a.Track, b.Track, t), RangeFill = Mix(a.RangeFill, b.RangeFill, t),
            Knob = Mix(a.Knob, b.Knob, t), Selection = Mix(a.Selection, b.Selection, t),
            Card = Mix(a.Card, b.Card, t), CardBorder = Mix(a.CardBorder, b.CardBorder, t), Shadow = Mix(a.Shadow, b.Shadow, t),
            Window = Mix(a.Window, b.Window, t),
        };
    }

    /// <summary>Linear blend of two colours including alpha.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte L(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A, t), L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }

    public static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

    /// <summary>The same colour with an absolute alpha (0..1).</summary>
    public static Color Alpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    /// <summary>RGB multiplied by k (alpha kept).</summary>
    public static Color Scale(Color c, double k)
    {
        static byte S(byte v, double k) => (byte)Math.Clamp(Math.Round(v * k), 0, 255);
        return Color.FromArgb(c.A, S(c.R, k), S(c.G, k), S(c.B, k));
    }
}

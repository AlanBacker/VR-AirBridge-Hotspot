using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// The app logo: a flat D-Link VR Air Bridge (the dongle with its X front and blue LED, standing in its dome)
/// sending Wi-Fi waves, on a rounded tile. One drawing for app.ico (written by --write-icon), the title bar,
/// the tray icon and the tray menu. Small sizes drop the fine detail so the shape still reads.
/// </summary>
internal static class AppLogo
{
    /// <summary>Tile colour: the light theme's accent, fixed so the logo looks the same everywhere.</summary>
    internal static readonly Color Tile = Color.FromRgb(70, 88, 230);

    private static readonly Brush TileBrush = Frozen(Tile);
    private static readonly Brush SideBrush = Frozen(Color.FromRgb(0x2C, 0x30, 0x38));
    private static readonly Brush TopBrush = Frozen(Color.FromRgb(0xBC, 0xC2, 0xCE));
    private static readonly Brush BottomBrush = Frozen(Color.FromRgb(0x14, 0x16, 0x1A));
    private static readonly Brush StandBrush = Frozen(Color.FromRgb(0x1C, 0x1F, 0x24));
    private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0x30, 0x34, 0x3C));
    private static readonly Brush StripeBrush = Frozen(Color.FromRgb(0x55, 0x5C, 0x68));
    private static readonly Brush LedBrush = Frozen(Color.FromRgb(0x1E, 0xA7, 0xFF));

    /// <summary>Pixel sizes written to app.ico.</summary>
    internal static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }

    private static Point P(double x, double y) => new(x, y);

    private static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts[1..], true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Point on the line a→b at parameter t (t &gt; 1 extends the line past b).</summary>
    private static Point Along(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>
    /// The logo on a 256-unit canvas. <paramref name="px"/> is the size it will be shown at in device pixels
    /// (picks the level of detail); <paramref name="badge"/> adds a status dot in the bottom-right corner (tray).
    /// </summary>
    internal static Drawing Create(int px, Color? badge = null)
    {
        bool fine = px >= 40;      // chevron stripes, two waves per side
        bool tiny = px <= 24;      // larger and wider dongle, no waves, no bottom facet
        var dg = new DrawingGroup();
        using (var g = dg.Open())
        {
            double r = 256 * 0.22;
            g.DrawRoundedRectangle(TileBrush, null, new Rect(0, 0, 256, 256), r, r);

            // The dongle: L..R wide, T..Bt tall, the X meets at (cx, cy) a little below the middle like the real one.
            g.PushTransform(tiny ? new ScaleTransform(1.26, 1.14, 128, 124) : Transform.Identity);
            const double L = 86, R = 170, T = 22, Bt = 174, cx = 128, cy = 106;
            var body = new StreamGeometry();
            using (var ctx = body.Open())
            {
                const double rr = 14;
                ctx.BeginFigure(P(L, Bt), true, true);
                ctx.LineTo(P(L, T + rr), true, true);
                ctx.ArcTo(P(L + rr, T), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(P(R - rr, T), true, true);
                ctx.ArcTo(P(R, T + rr), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, true);
                ctx.LineTo(P(R, Bt), true, true);
            }
            body.Freeze();

            var tl = P(L, T); var tr = P(R, T); var bl = P(L, Bt); var br = P(R, Bt); var mid = P(cx, cy);
            g.PushClip(body);
            g.DrawGeometry(SideBrush, null, body);
            if (fine)
            {
                // chevron grooves on the side facets, parallel to the X
                var stripe = Frozen(new Pen(StripeBrush, 6) { LineJoin = PenLineJoin.Miter });
                foreach (var s in new[] { 14.0, 28.0, 42.0 })
                {
                    foreach (int side in new[] { -1, 1 })
                    {
                        var off = new Vector(side * s, 0);
                        var f = new StreamGeometry();
                        using (var ctx = f.Open())
                        {
                            ctx.BeginFigure(Along(mid, side < 0 ? tl : tr, 1.3) + off, false, false);
                            ctx.LineTo(mid + off, true, false);
                            ctx.LineTo(Along(mid, side < 0 ? bl : br, 1.3) + off, true, false);
                        }
                        f.Freeze();
                        g.DrawGeometry(null, stripe, f);
                    }
                }
            }
            g.DrawGeometry(TopBrush, null, Poly(Along(mid, tl, 1.2), Along(mid, tr, 1.2), mid));
            if (!tiny) g.DrawGeometry(BottomBrush, null, Poly(Along(mid, bl, 1.2), Along(mid, br, 1.2), mid));
            g.Pop();

            // LED: blue downward triangle just above the middle of the X (larger than the real one so it reads)
            double lw = tiny ? 36 : 30, lh = tiny ? 28 : 24;
            var ledPen = Frozen(new Pen(LedBrush, 4) { LineJoin = PenLineJoin.Round });
            g.DrawGeometry(LedBrush, ledPen, Poly(P(cx - lw / 2, 76), P(cx + lw / 2, 76), P(cx, 76 + lh)));

            // stand: a dome with a flat top and a lighter front panel
            var stand = new StreamGeometry();
            using (var ctx = stand.Open())
            {
                ctx.BeginFigure(P(98, 180), true, true);
                ctx.LineTo(P(158, 180), true, true);
                ctx.BezierTo(P(182, 182), P(198, 204), P(198, 230), true, true);
                ctx.LineTo(P(58, 230), true, true);
                ctx.BezierTo(P(58, 204), P(74, 182), P(98, 180), true, true);
            }
            stand.Freeze();
            g.DrawGeometry(StandBrush, null, stand);
            if (!tiny)
            {
                g.PushClip(stand);
                g.DrawGeometry(PanelBrush, null, Poly(P(110, 170), P(146, 170), P(162, 240), P(94, 240)));
                g.Pop();
            }
            g.Pop();

            // Wi-Fi waves on both sides, centred on the LED
            if (!tiny)
            {
                var pen = Frozen(new Pen(Brushes.White, fine ? 11 : 16) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                var waves = fine ? new[] { (62.0, 26.0), (86.0, 24.0) } : new[] { (66.0, 26.0) };
                foreach (var (rad, deg) in waves)
                {
                    foreach (int side in new[] { -1, 1 })
                    {
                        double a0 = (side < 0 ? 180 : 0) - deg, a1 = (side < 0 ? 180 : 0) + deg;
                        Point At(double d) => P(cx + rad * Math.Cos(d * Math.PI / 180), 88 + rad * Math.Sin(d * Math.PI / 180));
                        var f = new StreamGeometry();
                        using (var ctx = f.Open())
                        {
                            ctx.BeginFigure(At(a0), false, false);
                            ctx.ArcTo(At(a1), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
                        }
                        f.Freeze();
                        g.DrawGeometry(null, pen, f);
                    }
                }
            }

            // status dot with a white ring (tray)
            if (badge is { } bc)
            {
                double br2 = tiny ? 50 : 46;
                var ring = Frozen(new Pen(Brushes.White, tiny ? 20 : 16));
                g.DrawEllipse(Frozen(bc), ring, P(256 - br2 - 8, 256 - br2 - 8), br2, br2);
            }
        }
        dg.Freeze();
        return dg;
    }

    /// <summary>The logo as a <paramref name="px"/> × <paramref name="px"/> bitmap.</summary>
    internal static BitmapSource Render(int px, Color? badge = null)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            dc.DrawDrawing(Create(px, badge));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Writes app.ico: one PNG frame per size in <see cref="IcoSizes"/>.</summary>
    internal static void WriteIco(string path)
    {
        var frames = IcoSizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(s)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Size: s, Png: ms.ToArray());
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
    }
}

/// <summary>The app logo at <see cref="Size"/> DIPs, drawn at the detail level that suits the screen's pixel size.</summary>
public sealed class AppLogoView : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppLogoView),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public AppLogoView()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double s = Size;
        if (s <= 0) return;
        int px = (int)Math.Round(s * VisualTreeHelper.GetDpi(this).DpiScaleX);
        dc.PushTransform(new TranslateTransform(Math.Max(0, (ActualWidth - s) / 2), Math.Max(0, (ActualHeight - s) / 2)));
        dc.PushTransform(new ScaleTransform(s / 256.0, s / 256.0));
        dc.DrawDrawing(AppLogo.Create(px));
        dc.Pop();
        dc.Pop();
    }
}

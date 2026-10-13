using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// Draws a Lucide icon (24×24 stroke geometry from Icons.xaml) at <see cref="Size"/> DIPs with the inherited
/// foreground, round caps / joins and a 2-unit stroke, optionally rotated around its centre.
/// </summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RotationProperty = DependencyProperty.Register(
        nameof(Rotation), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>"?" help icon: clicks are swallowed so a help icon inside a switch / button label does not toggle it.</summary>
    public static readonly DependencyProperty IsHelpProperty = DependencyProperty.Register(
        nameof(IsHelp), typeof(bool), typeof(IconView), new PropertyMetadata(false));

    public bool IsHelp { get => (bool)GetValue(IsHelpProperty); set => SetValue(IsHelpProperty, value); }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsHelp) e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsHelp) e.Handled = true;
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public double Rotation { get => (double)GetValue(RotationProperty); set => SetValue(RotationProperty, value); }

    public IconView()
    {
        SnapsToDevicePixels = false;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole icon box is hit-testable (tooltips / help icons).
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var g = Data;
        if (g == null || Size <= 0) return;
        double s = Size;
        double ox = Math.Max(0, (ActualWidth - s) / 2), oy = Math.Max(0, (ActualHeight - s) / 2);
        var pen = new Pen(Foreground, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        int pushes = 0;
        dc.PushTransform(new TranslateTransform(ox, oy)); pushes++;
        if (Rotation != 0) { dc.PushTransform(new RotateTransform(Rotation, s / 2, s / 2)); pushes++; }
        dc.PushTransform(new ScaleTransform(s / 24.0, s / 24.0)); pushes++;
        dc.DrawGeometry(null, pen, g);
        for (int i = 0; i < pushes; i++) dc.Pop();
    }
}

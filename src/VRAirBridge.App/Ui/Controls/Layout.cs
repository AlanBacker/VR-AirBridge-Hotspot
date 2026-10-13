using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// Vertical stack for the inside of a ScrollViewer: every child but the last takes its natural height,
/// the last child fills what is left of the viewport (at least <see cref="LastMinHeight"/>), so a list in
/// the last card keeps virtualizing while the page still scrolls when the window is short.
/// </summary>
public sealed class FillLastPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(FillLastPanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LastMinHeightProperty = DependencyProperty.Register(
        nameof(LastMinHeight), typeof(double), typeof(FillLastPanel),
        new FrameworkPropertyMetadata(170.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double LastMinHeight { get => (double)GetValue(LastMinHeightProperty); set => SetValue(LastMinHeightProperty, value); }

    private ScrollViewer? _sv;
    private double _lastH;

    public FillLastPanel()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        Unhook();
        DependencyObject? p = VisualTreeHelper.GetParent(this);
        while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
        _sv = p as ScrollViewer;
        if (_sv != null) _sv.SizeChanged += OnViewportChanged;
        InvalidateMeasure();
    }

    private void Unhook()
    {
        if (_sv != null) _sv.SizeChanged -= OnViewportChanged;
        _sv = null;
    }

    private void OnViewportChanged(object sender, SizeChangedEventArgs e) => InvalidateMeasure();

    private List<UIElement> Kids() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private double ViewportHeight(Size available)
    {
        if (!double.IsInfinity(available.Height)) return available.Height;
        if (_sv != null)
        {
            if (_sv.ViewportHeight > 0) return _sv.ViewportHeight;
            if (_sv.ActualHeight > 0) return _sv.ActualHeight;
        }
        return 600;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = Kids();
        foreach (UIElement c in InternalChildren)
            if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        double w = availableSize.Width;
        double used = 0, maxW = 0;
        for (int i = 0; i < kids.Count - 1; i++)
        {
            kids[i].Measure(new Size(w, double.PositiveInfinity));
            used += kids[i].DesiredSize.Height + Gap;
            maxW = Math.Max(maxW, kids[i].DesiredSize.Width);
        }
        _lastH = 0;
        if (kids.Count > 0)
        {
            var last = kids[^1];
            _lastH = Math.Max(LastMinHeight, ViewportHeight(availableSize) - used);
            last.Measure(new Size(w, _lastH));
            maxW = Math.Max(maxW, last.DesiredSize.Width);
        }
        double width = double.IsInfinity(w) ? maxW : w;
        return new Size(width, used + _lastH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = Kids();
        double y = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            bool last = i == kids.Count - 1;
            double h = last ? Math.Max(_lastH, finalSize.Height - y) : kids[i].DesiredSize.Height;
            kids[i].Arrange(new Rect(0, y, finalSize.Width, Math.Max(0, h)));
            y += h + Gap;
        }
        return finalSize;
    }
}

/// <summary>Horizontal wrap panel with uniform gaps (buttons that wrap instead of clipping).</summary>
public sealed class GapWrapPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(GapWrapPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(GapWrapPanel),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxW = availableSize.Width;
        double x = 0, y = 0, rowH = 0, widest = 0;
        bool first = true;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(maxW, double.PositiveInfinity));
            if (c.Visibility == Visibility.Collapsed) continue;
            var d = c.DesiredSize;
            if (!first && x + Gap + d.Width > maxW + 0.5)
            {
                y += rowH + RowGap;
                x = 0;
                rowH = 0;
                first = true;
            }
            x += (first ? 0 : Gap) + d.Width;
            rowH = Math.Max(rowH, d.Height);
            widest = Math.Max(widest, x);
            first = false;
        }
        return new Size(double.IsInfinity(maxW) ? widest : Math.Min(widest, maxW), y + rowH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowH = 0;
        bool first = true;
        var row = new List<UIElement>();
        void Flush()
        {
            double cx = 0;
            foreach (var e in row)
            {
                double w = Math.Min(e.DesiredSize.Width, finalSize.Width);
                e.Arrange(new Rect(cx, y + (rowH - e.DesiredSize.Height) / 2, w, e.DesiredSize.Height));
                cx += w + Gap;
            }
            row.Clear();
        }
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var d = c.DesiredSize;
            if (!first && x + Gap + d.Width > finalSize.Width + 0.5)
            {
                Flush();
                y += rowH + RowGap;
                x = 0;
                rowH = 0;
                first = true;
            }
            x += (first ? 0 : Gap) + d.Width;
            rowH = Math.Max(rowH, d.Height);
            row.Add(c);
            first = false;
        }
        Flush();
        return finalSize;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// Vertical stack for the inside of a ScrollViewer: every child but the last takes its natural height,
/// the last child fills what is left of the viewport (at least <see cref="LastMinHeight"/>), so a list in
/// the last card keeps virtualizing while the page still scrolls when the window is short.
/// When everything fits, the panel's height (including its own Margin) never exceeds the viewport, so the
/// page has nothing to scroll and the bottom of the last card stays above whatever sits below the viewer.
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
    private StableScrollViewer? _stable;
    // The ScrollContentPresenter between this panel and the ScrollViewer: its arranged height IS the viewport height,
    // already up to date when its SizeChanged fires. (ScrollViewer.ViewportHeight is only refreshed on LayoutUpdated,
    // after the re-measure we trigger, so reading it left the panel one resize behind: shrinking the window by 120 DIP
    // made the page 120 DIP too tall and hid the bottom of the log behind the status bar.)
    private FrameworkElement? _viewport;
    private FrameworkElement? _watched;
    private double _lastH;
    // Viewport height the last measure was based on: a viewport SizeChanged that matches it needs no second pass.
    private double _usedVh = double.NaN;

    public FillLastPanel()
    {
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        Unhook();
        DependencyObject? p = VisualTreeHelper.GetParent(this);
        while (p != null && p is not ScrollViewer)
        {
            if (_viewport == null && p is ScrollContentPresenter scp) _viewport = scp;
            p = VisualTreeHelper.GetParent(p);
        }
        _sv = p as ScrollViewer;
        if (_sv == null) _viewport = null;
        _stable = _sv as StableScrollViewer;
        if (_stable != null) _stable.ViewportHeightChanging += OnViewportHeightChanging;
        _watched = _viewport ?? _sv;
        if (_watched != null) _watched.SizeChanged += OnViewportChanged;
        InvalidateMeasure();
    }

    private void Unhook()
    {
        if (_stable != null) _stable.ViewportHeightChanging -= OnViewportHeightChanging;
        _stable = null;
        if (_watched != null) _watched.SizeChanged -= OnViewportChanged;
        _watched = null;
        _viewport = null;
        _sv = null;
    }

    private void OnViewportHeightChanging()
    {
        // The viewer is measuring and its content is next: dirty everything from here up to it, so that this very
        // pass re-measures the panel (an element in between whose own constraint did not change would stop it).
        for (DependencyObject? d = this; d != null && !ReferenceEquals(d, _sv); d = VisualTreeHelper.GetParent(d))
            (d as UIElement)?.InvalidateMeasure();
    }

    private void OnViewportChanged(object sender, SizeChangedEventArgs e)
    {
        // Inside a StableScrollViewer the measure already used this height (no second layout pass while resizing).
        if (e.HeightChanged && !(Math.Abs(e.NewSize.Height - _usedVh) <= 0.5)) InvalidateMeasure();
    }

    private List<UIElement> Kids() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    /// <summary>
    /// Height available to the children. Inside a ScrollViewer the measure height is infinite, so it comes from
    /// the viewer's viewport, minus this panel's own vertical Margin (FrameworkElement adds the Margin to the
    /// desired size afterwards; forgetting it made the page 28 DIP taller than the viewport, which pushed the
    /// bottom of the log card under the status bar and gave the page a scrollbar it never needed).
    /// </summary>
    private double ViewportHeight(Size available)
    {
        if (!double.IsInfinity(available.Height)) return available.Height;
        if (_sv != null)
        {
            double vh = _stable is { PendingViewportHeight: >= 0 } ? _stable.PendingViewportHeight
                : _viewport is { ActualHeight: > 0 } ? _viewport.ActualHeight
                : _sv.ViewportHeight > 0 ? _sv.ViewportHeight : _sv.ActualHeight;
            _usedVh = vh;
            if (vh > 0) return Math.Max(0, vh - Margin.Top - Margin.Bottom);
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
            // Floor: layout rounding of the other children (125 % DPI etc.) must never add up to a sub-pixel
            // overflow, which would be enough for the viewer to show a scrollbar; the arrange pass below still
            // stretches the last child over any remaining space.
            _lastH = Math.Max(LastMinHeight, Math.Floor(ViewportHeight(availableSize) - used));
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

/// <summary>
/// ScrollViewer whose VerticalScrollBarVisibility="Auto" lays the content out once. On every measure the stock
/// viewer hides the bar, lays the whole content out at the full width, then shows the bar and lays it all out again
/// at the narrower width, so each step of a window resize measured the sidebar, the dashboard and every visible log
/// line twice. This one keeps the bar as it was and measures a second time only when the content starts or stops
/// overflowing. It also hands the new viewport height to a <see cref="FillLastPanel"/> inside it before measuring
/// it, so the page fills the window in the same layout pass. Other values (and the horizontal bar) work as usual.
/// </summary>
public sealed class StableScrollViewer : ScrollViewer
{
    static StableScrollViewer()
    {
        // Auto is handled here: the base sees Visible, so it never hides and re-shows the bar by itself.
        VerticalScrollBarVisibilityProperty.OverrideMetadata(typeof(StableScrollViewer),
            new FrameworkPropertyMetadata(ScrollBarVisibility.Visible, null, CoerceVertical));
    }

    private ScrollBar? _bar;
    private bool _auto;
    /// <summary>Widest width at which the content was seen to overflow without the bar (NaN: unknown).</summary>
    private double _overflowsAt = double.NaN;

    private static object CoerceVertical(DependencyObject d, object value)
    {
        var sv = (StableScrollViewer)d;
        bool auto = (ScrollBarVisibility)value == ScrollBarVisibility.Auto;
        if (auto != sv._auto)
        {
            sv._auto = auto;
            if (!auto) sv._bar?.ClearValue(VisibilityProperty);
            else if (sv._bar != null) sv._bar.Visibility = Visibility.Collapsed;
            sv.InvalidateMeasure();
        }
        return auto ? ScrollBarVisibility.Visible : value;
    }

    /// <summary>Raised while measuring, before the content is measured, when the height it can show changes.</summary>
    internal event Action? ViewportHeightChanging;

    /// <summary>Viewport height of the measure in progress (NaN while the available height is unbounded).</summary>
    internal double PendingViewportHeight { get; private set; } = double.NaN;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // with Auto, a local Visibility on the bar (over the template's Visible) decides whether it takes its column
        _bar = GetTemplateChild("PART_VerticalScrollBar") as ScrollBar;
        if (_auto && _bar != null) _bar.Visibility = Visibility.Collapsed;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double vh = double.IsInfinity(constraint.Height) ? double.NaN : Math.Max(0, constraint.Height - Padding.Top - Padding.Bottom);
        if (!vh.Equals(PendingViewportHeight))
        {
            PendingViewportHeight = vh;
            ViewportHeightChanging?.Invoke();
        }
        var size = base.MeasureOverride(constraint);
        if (!_auto || _bar == null) return size;
        if (Overflows() != BarShown)
        {
            // Shown -> hidden only when the content fits even at the narrower width, so the wider second pass fits
            // too; hidden -> shown when it overflows at the full width, so the narrower pass overflows too: no flip-flop.
            if (!BarShown) _overflowsAt = constraint.Width;
            size = Remeasure(constraint, !BarShown);
        }
        else if (BarShown && NearFit() && !(constraint.Width <= _overflowsAt))
        {
            // Only just overflowing with the bar: like the stock Auto, no bar if the content fits at the full width.
            size = Remeasure(constraint, false);
            if (Overflows())
            {
                _overflowsAt = constraint.Width;
                size = Remeasure(constraint, true);
            }
        }
        return size;
    }

    private Size Remeasure(Size constraint, bool bar)
    {
        _bar!.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        if (VisualChildrenCount > 0 && GetVisualChild(0) is UIElement root) root.InvalidateMeasure();
        return base.MeasureOverride(constraint);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        // shorter content or a taller viewport may fit without the bar again
        if (e.ExtentHeightChange < 0 || e.ViewportHeightChange > 0) _overflowsAt = double.NaN;
        // the content grew or shrank by itself (a card expanded): the next measure picks the bar again
        if (_auto && _bar != null && (Overflows() != BarShown || BarShown && NearFit() && double.IsNaN(_overflowsAt)))
            InvalidateMeasure();
    }

    private bool BarShown => _bar!.Visibility == Visibility.Visible;

    private bool Overflows() => ScrollInfo is { } s && s.ExtentHeight - s.ViewportHeight > 0.01;

    /// <summary>Overflowing by so little that the few pixels of width the bar takes may be the reason.</summary>
    private bool NearFit() => ScrollInfo is { } s && s.ExtentHeight - s.ViewportHeight <= Math.Min(64, s.ViewportHeight / 4);
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

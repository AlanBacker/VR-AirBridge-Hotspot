using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VRAirBridge.App.Ui.Theme;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>Card with a clickable header (icon, title, chevron) whose body folds with <see cref="Reveal"/>.</summary>
public class SectionCard : HeaderedContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(SectionCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SectionCard),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HeaderExtraProperty = DependencyProperty.Register(
        nameof(HeaderExtra), typeof(object), typeof(SectionCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsCollapsibleProperty = DependencyProperty.Register(
        nameof(IsCollapsible), typeof(bool), typeof(SectionCard), new FrameworkPropertyMetadata(true));

    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public object? HeaderExtra { get => GetValue(HeaderExtraProperty); set => SetValue(HeaderExtraProperty, value); }
    public bool IsCollapsible { get => (bool)GetValue(IsCollapsibleProperty); set => SetValue(IsCollapsibleProperty, value); }

    static SectionCard()
    {
        FocusableProperty.OverrideMetadata(typeof(SectionCard), new FrameworkPropertyMetadata(false));
    }
}

/// <summary>Segmented control: a one-row ListBox with a sliding selection thumb.</summary>
public class Segmented : ListBox
{
    private FrameworkElement? _thumb;
    private FrameworkElement? _host;
    private readonly TranslateTransform _move = new();

    static Segmented()
    {
        SelectionModeProperty.OverrideMetadata(typeof(Segmented), new FrameworkPropertyMetadata(SelectionMode.Single));
    }

    public Segmented()
    {
        SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => MoveThumb(true)), System.Windows.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, _) => MoveThumb(false);
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => MoveThumb(false)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("PART_Thumb") as FrameworkElement;
        _host = GetTemplateChild("PART_Host") as FrameworkElement;
        if (_thumb != null) _thumb.RenderTransform = _move;
    }

    protected override DependencyObject GetContainerForItemOverride() => new SegmentedItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is SegmentedItem;

    private void MoveThumb(bool animate)
    {
        if (_thumb == null || _host == null) return;
        if (SelectedIndex < 0 || ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not FrameworkElement c || !c.IsVisible)
        {
            _thumb.Visibility = Visibility.Hidden;
            return;
        }
        Point p;
        try { p = c.TransformToAncestor(_host).Transform(new Point(0, 0)); }
        catch { return; }
        _thumb.Visibility = Visibility.Visible;
        _thumb.Width = c.ActualWidth;
        _thumb.Height = c.ActualHeight;
        if (animate && Motion.Enabled && _thumb.IsLoaded)
        {
            var a = new DoubleAnimation(p.X, Motion.Dur(0.18)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _move.BeginAnimation(TranslateTransform.XProperty, a);
        }
        else
        {
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = p.X;
        }
        _move.Y = p.Y;
    }
}

public class SegmentedItem : ListBoxItem { }

/// <summary>Rounded status pill: dot + text on a soft tone background.</summary>
public class Pill : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(Pill), new FrameworkPropertyMetadata(Tone.Normal));

    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot), typeof(bool), typeof(Pill), new FrameworkPropertyMetadata(true));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public bool ShowDot { get => (bool)GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }

    static Pill()
    {
        FocusableProperty.OverrideMetadata(typeof(Pill), new FrameworkPropertyMetadata(false));
    }
}

/// <summary>Status dot (r 4) with a soft halo (r 6, 22 %), like the reference.</summary>
public sealed class StatusDot : FrameworkElement
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(StatusDot),
        new FrameworkPropertyMetadata(Tone.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(StatusDot),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public StatusDot()
    {
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double d = Radius * 3;
        return new Size(d, d);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var b = ThemeManager.Brush(Tones.DotKey(Tone));
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.PushOpacity(0.22);
        dc.DrawEllipse(b, null, c, Radius * 1.5, Radius * 1.5);
        dc.Pop();
        dc.DrawEllipse(b, null, c, Radius, Radius);
    }
}

/// <summary>
/// Column grid that adapts to its width: as many columns (up to <see cref="MaxColumns"/>) as fit with
/// <see cref="MinColumnWidth"/>. With <see cref="AllOrNothing"/> it is either one row with every child or a single stacked column.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty AllOrNothingProperty = DependencyProperty.Register(
        nameof(AllOrNothing), typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BalancedProperty = DependencyProperty.Register(
        nameof(Balanced), typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty EqualHeightsProperty = DependencyProperty.Register(
        nameof(EqualHeights), typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool AllOrNothing { get => (bool)GetValue(AllOrNothingProperty); set => SetValue(AllOrNothingProperty, value); }
    public bool EqualHeights { get => (bool)GetValue(EqualHeightsProperty); set => SetValue(EqualHeightsProperty, value); }
    /// <summary>Only use column counts that divide the item count, so every row is full (4 items: 4, 2 or 1 columns).</summary>
    public bool Balanced { get => (bool)GetValue(BalancedProperty); set => SetValue(BalancedProperty, value); }

    private int _cols = 1;
    private readonly List<double> _rowHeights = new();

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int Columns(double width, int count)
    {
        if (count == 0) return 1;
        int max = Math.Max(1, Math.Min(MaxColumns, count));
        if (double.IsInfinity(width)) return max;
        int fit = (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap));
        fit = Math.Clamp(fit, 1, max);
        if (AllOrNothing && fit < count) fit = 1;
        if (Balanced)
            while (fit > 1 && count % fit != 0) fit--;
        return fit;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = Visible();
        foreach (UIElement c in InternalChildren)
            if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        _cols = Columns(availableSize.Width, kids.Count);
        double colW = double.IsInfinity(availableSize.Width) ? MinColumnWidth : Math.Max(0, (availableSize.Width - Gap * (_cols - 1)) / _cols);
        _rowHeights.Clear();
        double maxW = 0;
        for (int i = 0; i < kids.Count; i += _cols)
        {
            double h = 0;
            for (int j = i; j < Math.Min(i + _cols, kids.Count); j++)
            {
                kids[j].Measure(new Size(colW, double.PositiveInfinity));
                h = Math.Max(h, kids[j].DesiredSize.Height);
                maxW = Math.Max(maxW, kids[j].DesiredSize.Width);
            }
            _rowHeights.Add(h);
        }
        double total = _rowHeights.Sum() + Gap * Math.Max(0, _rowHeights.Count - 1);
        double width = double.IsInfinity(availableSize.Width) ? maxW * _cols + Gap * (_cols - 1) : availableSize.Width;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = Visible();
        double colW = Math.Max(0, (finalSize.Width - Gap * (_cols - 1)) / _cols);
        double y = 0;
        int row = 0;
        for (int i = 0; i < kids.Count; i += _cols, row++)
        {
            double h = row < _rowHeights.Count ? _rowHeights[row] : 0;
            for (int j = i; j < Math.Min(i + _cols, kids.Count); j++)
            {
                double x = (j - i) * (colW + Gap);
                double ch = EqualHeights ? h : kids[j].DesiredSize.Height;
                kids[j].Arrange(new Rect(x, y, colW, ch));
            }
            y += h + Gap;
        }
        return finalSize;
    }
}

/// <summary>Password box with a reveal toggle; <see cref="Password"/> binds two-way.</summary>
public sealed class PasswordField : Grid
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(PasswordField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(PasswordField),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRevealedChanged));

    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }
    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }

    private readonly PasswordBox _box = new() { MaxLength = 63 };
    private readonly TextBox _text = new() { MaxLength = 63, Visibility = Visibility.Collapsed };
    private bool _sync;

    public PasswordField()
    {
        Children.Add(_box);
        Children.Add(_text);
        _box.PasswordChanged += (_, _) => { if (!_sync) Update(_box.Password); };
        _text.TextChanged += (_, _) => { if (!_sync) Update(_text.Text); };
        AutomationPropertiesHelper.CopyName(this, _box);
        AutomationPropertiesHelper.CopyName(this, _text);
    }

    private void Update(string value)
    {
        _sync = true;
        try
        {
            Password = value;
            if (_box.Password != value) _box.Password = value;
            if (_text.Text != value) _text.Text = value;
        }
        finally { _sync = false; }
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (PasswordField)d;
        if (f._sync) return;
        var v = e.NewValue as string ?? "";
        f._sync = true;
        try
        {
            if (f._box.Password != v) f._box.Password = v;
            if (f._text.Text != v) f._text.Text = v;
        }
        finally { f._sync = false; }
    }

    private static void OnRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (PasswordField)d;
        bool show = (bool)e.NewValue;
        bool hadFocus = f._box.IsKeyboardFocusWithin || f._text.IsKeyboardFocusWithin;
        f._text.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        f._box.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (hadFocus)
        {
            if (show) { f._text.Focus(); f._text.CaretIndex = f._text.Text.Length; }
            else f._box.Focus();
        }
    }

    public void FocusInput()
    {
        if (IsRevealed) _text.Focus(); else _box.Focus();
    }
}

internal static class AutomationPropertiesHelper
{
    public static void CopyName(FrameworkElement from, FrameworkElement to)
    {
        to.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding("(AutomationProperties.Name)") { Source = from });
    }
}

/// <summary>Attached button properties used by the button templates (icon, icon size).</summary>
public static class Btn
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Btn), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(Btn), new FrameworkPropertyMetadata(16.0));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached(
        "Tone", typeof(Tone), typeof(Btn), new FrameworkPropertyMetadata(Tone.Normal));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Btn), new FrameworkPropertyMetadata(new CornerRadius(6)));

    /// <summary>ComboBox only: short text shown next to the icon when the box is compact (Tag="compact").</summary>
    public static readonly DependencyProperty ShortTextProperty = DependencyProperty.RegisterAttached(
        "ShortText", typeof(string), typeof(Btn), new FrameworkPropertyMetadata(null));

    public static string? GetShortText(DependencyObject o) => (string?)o.GetValue(ShortTextProperty);
    public static void SetShortText(DependencyObject o, string? v) => o.SetValue(ShortTextProperty, v);

    public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, CornerRadius v) => o.SetValue(RadiusProperty, v);
    public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, Geometry? v) => o.SetValue(IconProperty, v);
    public static double GetIconSize(DependencyObject o) => (double)o.GetValue(IconSizeProperty);
    public static void SetIconSize(DependencyObject o, double v) => o.SetValue(IconSizeProperty, v);
    public static Tone GetTone(DependencyObject o) => (Tone)o.GetValue(ToneProperty);
    public static void SetTone(DependencyObject o, Tone v) => o.SetValue(ToneProperty, v);
}

/// <summary>
/// Smooth mouse-wheel scrolling for a ScrollViewer (exponential approach, speed 18) that leaves the wheel
/// to inner scrollable lists while they can still scroll in that direction.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new FrameworkPropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollState), typeof(SmoothScroll), new FrameworkPropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

    private sealed class ScrollState
    {
        public double Target;
        public bool Running;
        public TimeSpan Last;
        public EventHandler? Tick;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
        else sv.PreviewMouseWheel -= OnWheel;
    }

    private static bool InnerCanScroll(ScrollViewer outer, DependencyObject? src, int delta)
    {
        while (src != null && src != outer)
        {
            if (src is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                if (delta > 0 && inner.VerticalOffset > 0) return true;
                if (delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5) return true;
            }
            if (src is Popup or ComboBox { IsDropDownOpen: true }) return true;
            src = src is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(src) : LogicalTreeHelper.GetParent(src);
        }
        return false;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (InnerCanScroll(sv, e.OriginalSource as DependencyObject, e.Delta)) return;
        if (sv.ScrollableHeight <= 0) return;
        e.Handled = true;
        var st = (ScrollState?)sv.GetValue(StateProperty);
        if (st == null) { st = new ScrollState(); sv.SetValue(StateProperty, st); }
        if (!st.Running) st.Target = sv.VerticalOffset;
        double step = e.Delta / 120.0 * 72;
        st.Target = Math.Clamp(st.Target - step, 0, sv.ScrollableHeight);
        if (!Motion.Enabled)
        {
            sv.ScrollToVerticalOffset(st.Target);
            return;
        }
        if (st.Running) return;
        st.Running = true;
        st.Last = TimeSpan.Zero;
        st.Tick = (_, args) =>
        {
            var now = args is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
            double dt = st.Last == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((now - st.Last).TotalSeconds, 0, 0.05);
            st.Last = now;
            st.Target = Math.Clamp(st.Target, 0, sv.ScrollableHeight);
            double v = Motion.Approach(sv.VerticalOffset, st.Target, 18, dt);
            if (Math.Abs(v - st.Target) < 0.5) v = st.Target;
            sv.ScrollToVerticalOffset(v);
            if (v == st.Target)
            {
                CompositionTarget.Rendering -= st.Tick;
                st.Running = false;
            }
        };
        CompositionTarget.Rendering += st.Tick;
    }
}

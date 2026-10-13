using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VRAirBridge.App.Ui.Theme;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// Expand / collapse container: height = child height × <see cref="Progress"/> with a matching fade,
/// animated over 0.2 s with smoothstep easing (the reference's section fold and Advanced reveal).
/// The child is collapsed when fully closed so it leaves the tab order.
/// </summary>
public sealed class Reveal : Decorator
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(Reveal), new FrameworkPropertyMetadata(true, OnIsOpenChanged));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Reveal),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange, OnProgressChanged));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(Reveal), new FrameworkPropertyMetadata(0.2));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    public Reveal()
    {
        Loaded += (_, _) => SyncImmediate();
    }

    private bool _initialized;

    private void SyncImmediate()
    {
        if (_initialized) return;
        _initialized = true;
        BeginAnimation(ProgressProperty, null);
        Progress = IsOpen ? 1 : 0;
        UpdateChild();
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var r = (Reveal)d;
        bool open = (bool)e.NewValue;
        if (!r.IsLoaded || !Motion.Enabled)
        {
            r.BeginAnimation(ProgressProperty, null);
            r.Progress = open ? 1 : 0;
            r.UpdateChild();
            return;
        }
        if (open && r.Child != null) r.Child.Visibility = Visibility.Visible;
        double from = r.Progress, to = open ? 1 : 0;
        double secs = r.Duration * Math.Abs(to - from);
        var anim = new DoubleAnimation(from, to, Motion.Dur(Math.Max(0.01, secs))) { EasingFunction = new SmoothstepEase(), FillBehavior = FillBehavior.HoldEnd };
        anim.Completed += (_, _) =>
        {
            r.BeginAnimation(ProgressProperty, null);
            r.Progress = r.IsOpen ? 1 : 0;
            r.UpdateChild();
        };
        r.BeginAnimation(ProgressProperty, anim);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var r = (Reveal)d;
        double p = (double)e.NewValue;
        r.Opacity = Math.Clamp(p, 0, 1);
        r.ClipToBounds = p < 0.999;
    }

    private void UpdateChild()
    {
        if (Child == null) return;
        Child.Visibility = Progress <= 0.001 && !IsOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var c = Child;
        if (c == null) return new Size(0, 0);
        c.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var ds = c.DesiredSize;
        double p = Math.Clamp(Progress, 0, 1);
        return new Size(ds.Width, Math.Ceiling(ds.Height * p - 0.0001));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var c = Child;
        if (c == null) return arrangeSize;
        double h = Math.Max(arrangeSize.Height, c.DesiredSize.Height);
        if (Progress < 0.999) h = c.DesiredSize.Height;
        c.Arrange(new Rect(0, 0, arrangeSize.Width, h));
        return arrangeSize;
    }
}

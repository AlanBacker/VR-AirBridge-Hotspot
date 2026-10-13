using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VRAirBridge.App.Ui.Theme;

namespace VRAirBridge.App.Ui.Controls;

/// <summary>
/// Fades an element in / out (with an optional vertical slide) when <c>IsShown</c> changes; collapses it when hidden.
/// The element should start with Visibility="Collapsed". With motion off (screenshots) the change is immediate.
/// </summary>
public static class FadeShow
{
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(FadeShow), new PropertyMetadata(false, OnShownChanged));

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(FadeShow), new PropertyMetadata(0.0));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached(
        "Duration", typeof(double), typeof(FadeShow), new PropertyMetadata(0.16));

    public static bool GetIsShown(DependencyObject o) => (bool)o.GetValue(IsShownProperty);
    public static void SetIsShown(DependencyObject o, bool v) => o.SetValue(IsShownProperty, v);
    public static double GetOffset(DependencyObject o) => (double)o.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject o, double v) => o.SetValue(OffsetProperty, v);
    public static double GetDuration(DependencyObject o) => (double)o.GetValue(DurationProperty);
    public static void SetDuration(DependencyObject o, double v) => o.SetValue(DurationProperty, v);

    private static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        var n = new TranslateTransform();
        el.RenderTransform = n;
        return n;
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        bool show = (bool)e.NewValue;
        double offset = GetOffset(el);
        var tt = Translate(el);
        if (!Motion.Enabled || !el.IsLoaded && show)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            el.Opacity = show ? 1 : 0;
            tt.Y = 0;
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && Motion.Enabled && offset != 0)
            {
                // first show before the element is loaded: still animate once it is
                el.Opacity = 0;
                tt.Y = offset;
                RoutedEventHandler? h = null;
                h = (_, _) => { el.Loaded -= h; Animate(el, tt, true, offset); };
                el.Loaded += h;
            }
            return;
        }
        Animate(el, tt, show, offset);
    }

    private static void Animate(FrameworkElement el, TranslateTransform tt, bool show, double offset)
    {
        var dur = TimeSpan.FromSeconds(GetDuration(el));
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        if (show)
        {
            el.Visibility = Visibility.Visible;
            var a = new DoubleAnimation(1, dur) { EasingFunction = ease };
            el.BeginAnimation(UIElement.OpacityProperty, a);
            if (offset != 0)
            {
                if (el.Opacity < 0.01) tt.Y = offset;
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
            }
        }
        else
        {
            var a = new DoubleAnimation(0, dur) { EasingFunction = ease };
            a.Completed += (_, _) =>
            {
                if (!GetIsShown(el)) el.Visibility = Visibility.Collapsed;
            };
            el.BeginAnimation(UIElement.OpacityProperty, a);
            if (offset != 0) tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset * 0.5, dur) { EasingFunction = ease });
        }
    }
}

/// <summary>Toast motion: slides in from the right and fades in when loaded; slides / fades out when <c>Leaving</c>.</summary>
public static class ToastMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ToastMotion), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty LeavingProperty = DependencyProperty.RegisterAttached(
        "Leaving", typeof(bool), typeof(ToastMotion), new PropertyMetadata(false, OnLeavingChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetLeaving(DependencyObject o) => (bool)o.GetValue(LeavingProperty);
    public static void SetLeaving(DependencyObject o, bool v) => o.SetValue(LeavingProperty, v);

    private const double Distance = 40;

    private static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        var n = new TranslateTransform();
        el.RenderTransform = n;
        return n;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || !(bool)e.NewValue) return;
        if (!Motion.Enabled) return;
        var tt = Translate(el);
        el.Opacity = 0;
        tt.X = Distance;
        el.Loaded += (_, _) =>
        {
            if (GetLeaving(el)) return;
            var dur = TimeSpan.FromSeconds(0.28);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        };
    }

    private static void OnLeavingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || !(bool)e.NewValue) return;
        if (!Motion.Enabled) { el.Opacity = 0; return; }
        var tt = Translate(el);
        var dur = TimeSpan.FromSeconds(0.25);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, dur) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Distance, dur) { EasingFunction = ease });
    }
}

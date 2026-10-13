using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using VRAirBridge.Core;

namespace VRAirBridge.App.Ui.Theme;

/// <summary>
/// Owns every themed brush / effect in the application resources. Brushes are created once,
/// never frozen and referenced with DynamicResource, so a theme change only mutates their colours
/// in place. Follows the Windows "AppsUseLightTheme" setting unless the user picked dark / light,
/// and blends between the two palettes with an exponential approach (speed 9, like the reference).
/// </summary>
internal static class ThemeManager
{
    public const string SystemSetting = "system";
    public const string DarkSetting = "dark";
    public const string LightSetting = "light";

    private const double BlendSpeed = 9;

    private static ResourceDictionary? _res;
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DropShadowEffect> Effects = new(StringComparer.Ordinal);
    private static readonly List<WeakReference<Window>> Windows = new();
    private static double _t, _target;
    private static string _setting = SystemSetting;
    private static bool _rendering;
    private static TimeSpan _lastFrame;
    private static bool? _lastDarkCaption;

    /// <summary>Raised when the effective theme (dark / light) changes, at the start of the blend.</summary>
    public static event Action? Changed;

    public static bool IsLight => _target >= 0.5;
    public static double BlendValue => _t;
    public static string Setting => _setting;

    public static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        DarkSetting => DarkSetting,
        LightSetting => LightSetting,
        _ => SystemSetting,
    };

    public static void Initialize(ResourceDictionary res, string? setting)
    {
        _res = res;
        _setting = Normalize(setting);
        _target = _t = EffectiveLight() ? 1 : 0;
        Apply();
    }

    public static void SetSetting(string? setting)
    {
        _setting = Normalize(setting);
        Retarget();
    }

    /// <summary>Re-reads the Windows setting (WM_SETTINGCHANGE "ImmersiveColorSet").</summary>
    public static void SystemThemeChanged()
    {
        if (_setting == SystemSetting) Retarget();
    }

    private static void Retarget()
    {
        double target = EffectiveLight() ? 1 : 0;
        if (target == _target) return;
        _target = target;
        try { Changed?.Invoke(); } catch { }
        if (!Motion.Enabled || _res == null)
        {
            _t = _target;
            Apply();
            return;
        }
        if (!_rendering)
        {
            _rendering = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        _t += (_target - _t) * (1 - Math.Exp(-BlendSpeed * dt));
        if (Math.Abs(_target - _t) < 0.003)
        {
            _t = _target;
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
        Apply();
    }

    public static bool EffectiveLight() => _setting switch
    {
        LightSetting => true,
        DarkSetting => false,
        _ => SystemUsesLightTheme(),
    };

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return true;
    }

    // ------------------------------------------------------------------ resources

    /// <summary>
    /// Off-screen rendering (RenderTargetBitmap) keeps showing the old colour of a brush that was mutated in place,
    /// so the screenshot tool asks for new brush / effect instances on every theme change.
    /// </summary>
    internal static bool RecreateOnApply { get; set; }

    private static void SetBrush(string key, Color c)
    {
        if (_res == null) return;
        if (Brushes.TryGetValue(key, out var b) && !b.IsFrozen && !RecreateOnApply)
        {
            if (b.Color != c) b.Color = c;
            return;
        }
        b = Unfreezable(new SolidColorBrush(c), System.Windows.Media.Brush.OpacityProperty, 1.0);
        Brushes[key] = b;
        _res[key] = b;
    }

    /// <summary>
    /// Application-level resource dictionaries seal (freeze) every Freezable added to them, which would make the
    /// in-place colour blend impossible. A Freezable with a binding cannot be frozen, so each themed brush / effect
    /// gets a constant binding on a property this class never sets.
    /// </summary>
    private static T Unfreezable<T>(T f, DependencyProperty dp, object value) where T : Freezable
    {
        BindingOperations.SetBinding(f, dp, new Binding { Source = value, Mode = BindingMode.OneWay });
        return f;
    }

    private static void SetShadow(string key, Color c, double opacity, double blur, double depth)
    {
        if (_res == null) return;
        var rgb = Color.FromRgb(c.R, c.G, c.B);
        opacity = Math.Clamp(opacity, 0, 1);
        if (Effects.TryGetValue(key, out var e) && !e.IsFrozen && !RecreateOnApply)
        {
            if (e.Color != rgb) e.Color = rgb;
            if (e.Opacity != opacity) e.Opacity = opacity;
            if (e.BlurRadius != blur) e.BlurRadius = blur;
            if (e.ShadowDepth != depth) e.ShadowDepth = depth;
            return;
        }
        e = Unfreezable(new DropShadowEffect
        {
            RenderingBias = RenderingBias.Performance,
            Color = rgb,
            Opacity = opacity,
            BlurRadius = blur,
            ShadowDepth = depth,
        }, DropShadowEffect.DirectionProperty, 270.0);
        Effects[key] = e;
        _res[key] = e;
    }

    /// <summary>Looks up a themed brush (for code that cannot use DynamicResource).</summary>
    public static Brush Brush(string key) =>
        Brushes.TryGetValue(key, out var b) ? b : (Application.Current?.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Transparent);

    public static Color ColorOf(string key) => Brushes.TryGetValue(key, out var b) ? b.Color : Colors.Transparent;

    private static void Apply()
    {
        double l = _t;
        var p = Palette.Blend(l);
        var white = Colors.White;
        var black = Colors.Black;

        SetBrush("B.Window", p.Window);
        SetBrush("B.Card", p.Card);
        SetBrush("B.CardBorder", p.CardBorder);
        SetBrush("B.Panel", p.Panel);
        SetBrush("B.PanelBorder", p.PanelBorder);
        SetBrush("B.Surface", p.Surface);
        SetBrush("B.Overlay", p.OverlayBg);
        SetBrush("B.Text", p.Text);
        SetBrush("B.TextDim", p.TextDim);
        SetBrush("B.Muted", p.Muted);
        SetBrush("B.Accent", p.Accent);
        SetBrush("B.AccentHover", p.AccentHover);
        SetBrush("B.AccentActive", p.AccentActive);
        SetBrush("B.AccentText", p.AccentText);
        SetBrush("B.Good", p.Good);
        SetBrush("B.Warn", p.Warn);
        SetBrush("B.Bad", p.Bad);
        SetBrush("B.GoodSoft", Palette.Alpha(p.Good, 0.16));
        SetBrush("B.WarnSoft", Palette.Alpha(p.Warn, 0.16));
        SetBrush("B.BadSoft", Palette.Alpha(p.Bad, 0.16));
        SetBrush("B.MutedSoft", Palette.Alpha(p.TextDim, 0.16));
        SetBrush("B.AccentSoft", Palette.Alpha(p.Accent, 0.22 - 0.10 * l));
        SetBrush("B.AccentFaint", Palette.Alpha(p.Accent, 0.05));
        SetBrush("B.Control", p.Control);
        SetBrush("B.ControlHover", p.ControlHover);
        SetBrush("B.ControlActive", p.ControlActive);
        SetBrush("B.Track", p.Track);
        SetBrush("B.Knob", white);
        SetBrush("B.ToggleOff", Palette.Mix(p.ControlActive, p.TextDim, 0.18));
        SetBrush("B.ToggleOffHover", Palette.Mix(p.ControlActive, p.TextDim, 0.30));
        SetBrush("B.PopupBg", Palette.Mix(Palette.Mix(p.Card, p.Control, 0.45), p.Card, l));
        SetBrush("B.ScrollThumb", Palette.Mix(Color.FromRgb(60, 65, 78), Color.FromRgb(198, 203, 213), l));
        SetBrush("B.ScrollThumbHover", Palette.Mix(Color.FromRgb(80, 87, 104), Color.FromRgb(172, 178, 191), l));
        SetBrush("B.ScrollThumbActive", Palette.Mix(Color.FromRgb(98, 106, 126), Color.FromRgb(150, 157, 172), l));
        SetBrush("B.Selection", Palette.Alpha(p.Accent, Palette.Lerp(0.22, 0.14, l)));
        SetBrush("B.TextSelection", Palette.Alpha(p.Accent, Palette.Lerp(0.40, 0.28, l)));
        SetBrush("B.Link", Palette.Mix(p.AccentHover, p.Accent, l));
        SetBrush("B.Scrim", Palette.Alpha(black, Palette.Lerp(0.55, 0.30, l)));
        SetBrush("B.AltRow", Palette.Alpha(p.Text, 0.03));
        SetBrush("B.GhostBorder", Palette.Mix(p.CardBorder, p.TextDim, 0.25));
        SetBrush("B.CheckBorder", Palette.Mix(p.ControlActive, p.TextDim, 0.35));
        SetBrush("B.SegThumb", Palette.Mix(Palette.Mix(p.ControlActive, white, 0.05), p.Card, l));
        SetBrush("B.IconAccent", Palette.Mix(p.AccentHover, p.Accent, l));
        SetBrush("B.Help", Palette.Scale(p.TextDim, 0.85));
        SetBrush("B.HelpHover", p.AccentHover);
        SetBrush("B.SectionHover", Palette.Alpha(p.ControlHover, 0.55));
        SetBrush("B.SectionPress", Palette.Alpha(p.ControlHover, 0.90));
        SetBrush("B.Focus", Palette.Alpha(p.Accent, 0.75));
        SetBrush("B.DangerHover", Palette.Mix(p.Bad, white, 0.14));
        SetBrush("B.DangerActive", Palette.Mix(p.Bad, black, 0.18));
        SetBrush("B.HoverTint", Palette.Mix(Palette.Alpha(white, 0.08), Palette.Alpha(black, 0.05), l));
        SetBrush("B.PressTint", Palette.Mix(Palette.Alpha(white, 0.14), Palette.Alpha(black, 0.10), l));
        SetBrush("B.WarnBg", Palette.Alpha(p.Warn, Palette.Lerp(0.10, 0.09, l)));
        SetBrush("B.WarnBorder", Palette.Alpha(p.Warn, 0.35));
        SetBrush("B.BadBg", Palette.Alpha(p.Bad, Palette.Lerp(0.10, 0.07, l)));
        SetBrush("B.BadBorder", Palette.Alpha(p.Bad, 0.35));
        SetBrush("B.AccentBg", Palette.Alpha(p.Accent, Palette.Lerp(0.10, 0.07, l)));
        SetBrush("B.AccentBorder", Palette.Alpha(p.Accent, 0.30));
        SetBrush("B.Divider", p.CardBorder);

        double sa = p.Shadow.A / 255.0;
        double k = sa * (0.35 + 0.4 * l);
        SetShadow("E.Card", p.Shadow, k, 16, 2);
        SetShadow("E.Popup", p.Shadow, k * 1.4, 32, 5);
        SetShadow("E.Toast", p.Shadow, k * 1.4, 28, 4);
        SetShadow("E.Knob", black, 0.28, 3, 1);

        ApplyCaptions(p);
    }

    // ------------------------------------------------------------------ fonts

    /// <summary>Segoe UI for Latin text, then CJK fonts ordered for the UI language.</summary>
    public static FontFamily FontFor(string lang) => new("Segoe UI, " + CjkFallback(lang) + ", Segoe UI Symbol");

    /// <summary>Monospace (log, result lines) with the same CJK fallback order.</summary>
    public static FontFamily MonoFor(string lang) => new("Cascadia Mono, Consolas, " + CjkFallback(lang) + ", Segoe UI Symbol");

    private static string CjkFallback(string lang) => lang switch
    {
        Loc.Japanese => "Yu Gothic UI, Meiryo UI, Meiryo, Microsoft YaHei UI, Malgun Gothic",
        Loc.Korean => "Malgun Gothic, Microsoft YaHei UI, Yu Gothic UI",
        Loc.ChineseSimplified => "Microsoft YaHei UI, Microsoft YaHei, Yu Gothic UI, Malgun Gothic",
        _ => "Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic",
    };

    public static void ApplyLanguage(string lang)
    {
        if (_res == null) return;
        _res["F.Ui"] = FontFor(lang);
        _res["F.Mono"] = MonoFor(lang);
        foreach (var w in LiveWindows()) SetWindowLanguage(w, lang);
    }

    private static void SetWindowLanguage(Window w, string lang)
    {
        try
        {
            var tag = Loc.CultureFor(lang).IetfLanguageTag;
            w.Language = System.Windows.Markup.XmlLanguage.GetLanguage(string.IsNullOrEmpty(tag) ? "en-US" : tag);
        }
        catch { }
    }

    // ------------------------------------------------------------------ windows (DWM caption + WM_SETTINGCHANGE)

    private static IEnumerable<Window> LiveWindows()
    {
        Windows.RemoveAll(r => !r.TryGetTarget(out _));
        var list = new List<Window>();
        foreach (var r in Windows)
            if (r.TryGetTarget(out var w)) list.Add(w);
        return list;
    }

    /// <summary>Registers a window: dark caption, caption colour, theme / language tracking.</summary>
    public static void Attach(Window w)
    {
        Windows.Add(new WeakReference<Window>(w));
        SetWindowLanguage(w, Loc.Current);
        w.SetResourceReference(Window.FontFamilyProperty, "F.Ui");
        void Hook()
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
            src?.AddHook(WndProc);
            _lastDarkCaption = null;
            ApplyCaptions(Palette.Blend(_t));
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Hook();
        else w.SourceInitialized += (_, _) => Hook();
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            try
            {
                if (Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") SystemThemeChanged();
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void ApplyCaptions(Palette p)
    {
        bool dark = _t < 0.5;
        bool darkChanged = _lastDarkCaption != dark;
        _lastDarkCaption = dark;
        foreach (var w in LiveWindows())
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) continue;
            try
            {
                if (darkChanged)
                {
                    int v = dark ? 1 : 0;
                    DwmSetWindowAttribute(h, 20, ref v, 4);
                }
                int cap = p.Window.R | (p.Window.G << 8) | (p.Window.B << 16);
                DwmSetWindowAttribute(h, 35, ref cap, 4);
                int txt = p.Text.R | (p.Text.G << 8) | (p.Text.B << 16);
                DwmSetWindowAttribute(h, 36, ref txt, 4);
            }
            catch { }
        }
    }
}

/// <summary>Global motion switch (screenshots render with motion off) and shared easing.</summary>
internal static class Motion
{
    public static bool Enabled { get; set; } = true;

    public static Duration Dur(double seconds) =>
        new(Enabled ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    public static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Exponential approach used by the reference for hover / toggle / popup motion.</summary>
    public static double Approach(double v, double target, double speed, double dt) =>
        v + (target - v) * (1 - Math.Exp(-speed * dt));
}

/// <summary>Smoothstep easing (matches the reference's section fold / reveal).</summary>
internal sealed class SmoothstepEase : System.Windows.Media.Animation.EasingFunctionBase
{
    protected override double EaseInCore(double normalizedTime) => Motion.Smoothstep(normalizedTime);
    protected override Freezable CreateInstanceCore() => new SmoothstepEase();
}

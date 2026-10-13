using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VRAirBridge.App.Ui.Views;

/// <summary>Visual of one dialog; shared by the in-window <see cref="DialogLayer"/> and the standalone <see cref="DialogWindow"/>.</summary>
public partial class DialogCard : UserControl
{
    public static readonly DependencyProperty BodyMaxHeightProperty = DependencyProperty.Register(
        nameof(BodyMaxHeight), typeof(double), typeof(DialogCard), new PropertyMetadata(440.0));

    public double BodyMaxHeight { get => (double)GetValue(BodyMaxHeightProperty); set => SetValue(BodyMaxHeightProperty, value); }

    public DialogCard()
    {
        InitializeComponent();
    }

    internal FrameworkElement DragHandle => TitleRow;

    /// <summary>Focuses the primary button (or the first one).</summary>
    internal void FocusPrimary()
    {
        UpdateLayout();
        var b = FindButton(ButtonList, primary: true) ?? FindButton(ButtonList, primary: false);
        b?.Focus();
    }

    private static Button? FindButton(DependencyObject root, bool primary)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Button b && (!primary || Equals(b.Tag, "primary"))) return b;
            var r = FindButton(c, primary);
            if (r != null) return r;
        }
        return null;
    }
}

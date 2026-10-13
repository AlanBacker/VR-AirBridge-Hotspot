using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>Dashboard: hotspot, channel, devices and log cards. Code-behind only keeps the log scrolled to the end.</summary>
public partial class DashboardView : UserControl
{
    // Corrective ScrollToEnd calls allowed before the list reports that it reached its end (the virtualizing panel
    // estimates the height of lines it has not measured yet, so the first jump can land a little short).
    private const int MaxChase = 20;

    private MainViewModel? _vm;
    private INotifyCollectionChanged? _lines;
    private ScrollViewer? _logScroll;
    // True while the log follows its newest line: it then stays pinned to the end when lines are added or its height
    // changes (window resized, a card above it grows). Only the user turns this off, by scrolling up (wheel, keys,
    // scrollbar); reaching the end again turns it back on. Offset changes the list makes by itself (estimated heights
    // being corrected, re-anchoring) never do, so the newest line cannot drift out of view.
    private bool _followTail = true;
    private bool _onScrollBar;
    private int _chase;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        LogList.Loaded += (_, _) =>
        {
            LogScroll();
            ScrollLogToEnd();
        };
        LogList.PreviewKeyDown += OnLogKey;
        // The log scrolls inside its own ScrollViewer; never let it drag the whole dashboard along
        // (in the compact layout the outer ScrollViewer would otherwise jump down to the log).
        LogList.RequestBringIntoView += (_, e) => e.Handled = true;
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.LogReset -= OnLogReset;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.LogReset += OnLogReset;
            _vm.PropertyChanged += OnVmChanged;
        }
        HookLines();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogLines)) { HookLines(); OnLogReset(); }
    }

    private void HookLines()
    {
        if (_lines != null) _lines.CollectionChanged -= OnLinesChanged;
        _lines = _vm?.LogLines;
        if (_lines != null) _lines.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // follow the tail only when the user has not scrolled up
        if (_followTail || LogScroll() == null)
        {
            _chase = 0;
            ScrollLogToEnd();
        }
    }

    private void OnLogReset()
    {
        _followTail = true;
        _chase = 0;
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogScroll()?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private ScrollViewer? LogScroll()
    {
        if (_logScroll != null) return _logScroll;
        _logScroll = Find<ScrollViewer>(LogList);
        if (_logScroll != null)
        {
            _logScroll.ScrollChanged += OnLogScrollChanged;
            _logScroll.PreviewMouseWheel += OnLogWheel;
            _logScroll.PreviewMouseLeftButtonDown += OnLogMouseDown;
            _logScroll.PreviewMouseLeftButtonUp += OnLogMouseUp;
            _logScroll.LostMouseCapture += OnLogMouseUp;
        }
        return _logScroll;
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || !ReferenceEquals(e.OriginalSource, sv)) return;
        if (AtEnd(sv))
        {
            _chase = 0;
            // scrolled (back) down to the newest line, or nothing to scroll at all
            if (e.VerticalChange > 0 || _onScrollBar || sv.ScrollableHeight <= 0.5) _followTail = true;
            return;
        }
        if (_onScrollBar && e.VerticalChange != 0) _followTail = false; // dragging / clicking the scrollbar upwards
        if (_followTail && _chase++ < MaxChase) sv.ScrollToEnd();
    }

    private void OnLogWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && sender is ScrollViewer { ScrollableHeight: > 0 }) _followTail = false;
    }

    private void OnLogKey(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Up or Key.PageUp or Key.Home) && _logScroll is { ScrollableHeight: > 0 }) _followTail = false;
    }

    private void OnLogMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ScrollBar>(d, (DependencyObject)sender) != null) _onScrollBar = true;
    }

    private void OnLogMouseUp(object sender, EventArgs e)
    {
        if (!_onScrollBar) return;
        _onScrollBar = false;
        if (_logScroll != null) _followTail = AtEnd(_logScroll);
    }

    private static bool AtEnd(ScrollViewer sv) => sv.VerticalOffset >= sv.ScrollableHeight - 0.5;

    private static T? FindAncestor<T>(DependencyObject d, DependencyObject stop) where T : DependencyObject
    {
        for (var p = d; p != null && !ReferenceEquals(p, stop); p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = Find<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}

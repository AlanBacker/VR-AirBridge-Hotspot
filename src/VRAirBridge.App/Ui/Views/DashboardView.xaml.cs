using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>Dashboard: hotspot, channel, devices and log cards. Code-behind only keeps the log scrolled to the end.</summary>
public partial class DashboardView : UserControl
{
    private MainViewModel? _vm;
    private INotifyCollectionChanged? _lines;
    private ScrollViewer? _logScroll;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        LogList.Loaded += (_, _) => ScrollLogToEnd();
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
        var sv = LogScroll();
        // follow the tail only when the user has not scrolled up
        if (sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 2) ScrollLogToEnd();
    }

    private void OnLogReset() => ScrollLogToEnd();

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
        return _logScroll;
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

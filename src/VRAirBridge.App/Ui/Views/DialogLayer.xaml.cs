using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VRAirBridge.App.Ui.ViewModels;

namespace VRAirBridge.App.Ui.Views;

/// <summary>Overlay that shows the current <see cref="DialogHostVm"/> dialog; focuses its primary button.</summary>
public partial class DialogLayer : UserControl
{
    private DialogHostVm? _host;
    private IInputElement? _returnFocus;

    public DialogLayer()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_host != null) _host.PropertyChanged -= OnHostChanged;
            _host = e.NewValue as DialogHostVm;
            if (_host != null) _host.PropertyChanged += OnHostChanged;
        };
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_host == null) return;
        if (e.PropertyName == nameof(DialogHostVm.Current) && _host.Current != null)
        {
            if (_returnFocus == null && Keyboard.FocusedElement is DependencyObject d && !IsAncestorOf(d))
                _returnFocus = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(() => Card.FocusPrimary(), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(DialogHostVm.IsOpen) && !_host.IsOpen)
        {
            var back = _returnFocus;
            _returnFocus = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_host?.IsOpen == true) return;
                if (back is UIElement u && u.IsVisible && u.Focusable) u.Focus();
            }, DispatcherPriority.Background);
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        _host?.Cancel();
        e.Handled = true;
    }
}

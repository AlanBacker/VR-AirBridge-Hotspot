using VRAirBridge.Core;

namespace VRAirBridge.App.Ui.Services;

/// <summary>What the view model needs from the window / tray layer (implemented by AppController, mocked for screenshots).</summary>
internal interface IUiHost
{
    /// <summary>The main window is shown (it may be minimized).</summary>
    bool IsWindowShown { get; }

    /// <summary>The main window is shown and not minimized.</summary>
    bool IsWindowVisible { get; }

    void ShowWindow();
    void HideToTray();
    Task ExitAsync(bool stopHotspot, bool ask);
    void UpdateTray(AppStatus status);
    void SetTrayInternet(bool allow);
    void Balloon(Notification n);
    void CopyText(string text);
}

/// <summary>Host used when rendering screenshots: nothing leaves the process.</summary>
internal sealed class NullUiHost : IUiHost
{
    public bool IsWindowShown => true;
    public bool IsWindowVisible => true;
    public void ShowWindow() { }
    public void HideToTray() { }
    public Task ExitAsync(bool stopHotspot, bool ask) => Task.CompletedTask;
    public void UpdateTray(AppStatus status) { }
    public void SetTrayInternet(bool allow) { }
    public void Balloon(Notification n) { }
    public void CopyText(string text) { }
}

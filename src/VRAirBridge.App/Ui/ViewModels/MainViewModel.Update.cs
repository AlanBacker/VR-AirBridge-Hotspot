using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using VRAirBridge.App.Ui.Theme;
using VRAirBridge.Core;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Update;

namespace VRAirBridge.App.Ui.ViewModels;

public enum UpdatePhase { None, Available, Downloading, Installing, Failed }

/// <summary>
/// Update check (GitHub "releases/latest") and self-update: banner above the page, status in the About section,
/// a tray balloon when the window is hidden. Nothing is downloaded before the user clicks "Update now".
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan UpdateTick = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuickRetry = TimeSpan.FromMinutes(5);

    private DispatcherTimer? _updateTimer;
    private UpdateChecker? _checker;
    private CancellationTokenSource? _updateCts;
    private ReleaseInfo? _release;          // newer release offered in the banner
    private ReleaseInfo? _skippedRelease;   // newer release the user skipped (About text only)
    private UpdatePhase _updatePhase;
    private UpdateCheckStatus? _checkStatus;
    private string? _checkError, _updateError;
    private UpdateProblem _updateProblem;
    private DateTime? _checkFailedUtc;
    private int _checkFailures;
    private bool _checking, _checkedThisRun, _updateHidden;
    private double _updatePercent;
    private string? _balloonVersion;

    private ICommand? _updateInstallCmd, _updateSkipCmd, _updateLaterCmd, _updateNotesCmd, _updateCancelCmd, _checkUpdatesCmd, _showQrCmd;

    public ICommand UpdateInstallCommand => _updateInstallCmd ??= new RelayCommand(() => _ = InstallUpdateAsync());
    public ICommand UpdateSkipCommand => _updateSkipCmd ??= new RelayCommand(SkipUpdate);
    public ICommand UpdateLaterCommand => _updateLaterCmd ??= new RelayCommand(() => { _updateHidden = true; RaiseUpdate(); });
    public ICommand UpdateNotesCommand => _updateNotesCmd ??= new RelayCommand(() => OpenUrl(ReleasePageUrl(_release ?? _skippedRelease)));
    public ICommand UpdateCancelCommand => _updateCancelCmd ??= new RelayCommand(() => _updateCts?.Cancel());
    public ICommand CheckUpdatesCommand => _checkUpdatesCmd ??= new RelayCommand(() => _ = CheckUpdatesAsync(manual: true));
    public ICommand ShowQrCommand => _showQrCmd ??= new RelayCommand(() => _ = ShowQrAsync());

    // ================================================================== banner

    public UpdatePhase UpdatePhase => _updatePhase;
    public bool ShowUpdateBanner => _release != null && _updatePhase != UpdatePhase.None && (!_updateHidden || UpdateBusy);
    public bool UpdateBusy => _updatePhase is UpdatePhase.Downloading or UpdatePhase.Installing;
    public bool UpdateFailed => _updatePhase == UpdatePhase.Failed;
    public bool UpdateDownloading => _updatePhase == UpdatePhase.Downloading;
    public double UpdatePercent => _updatePercent;
    public Tone UpdateTone => UpdateFailed ? Tone.Bad : Tone.Accent;
    public string UpdateIcon => UpdateFailed ? "I.circle-alert" : "I.circle-arrow-up";

    public string UpdateTitle => _release == null ? ""
        : UpdateFailed ? Loc.T("ui.update.failedTitle")
        : Loc.T("ui.update.availableTitle", _release.DisplayVersion);

    public string UpdateText => _release == null ? "" : _updatePhase switch
    {
        UpdatePhase.Downloading => _updatePercent >= 100
            ? Loc.T("ui.update.verifying")
            : Loc.T("ui.update.downloading", _release.DisplayVersion, ((int)_updatePercent).ToString(CultureInfo.InvariantCulture)),
        UpdatePhase.Installing => Loc.T("ui.update.installing"),
        UpdatePhase.Failed => _updateError ?? "",
        _ => Loc.T("ui.update.availableText", AppInfo.DisplayVersion),
    };

    /// <summary>The release can be installed here (exe attached, SHA-256 known): otherwise only the download page is offered.</summary>
    private bool CanAutoInstall => _release != null && UpdateInstaller.CanInstall(_release) == UpdateProblem.None
                                   && _updateProblem is not (UpdateProblem.NotWritable or UpdateProblem.Quarantined);

    public bool ShowUpdateInstall => !UpdateBusy && CanAutoInstall;
    public string UpdateInstallText => Loc.T(UpdateFailed ? "ui.update.retry" : "ui.update.install");
    public bool ShowUpdateSkip => _updatePhase == UpdatePhase.Available;
    public bool ShowUpdatePage => !UpdateBusy && (UpdateFailed || !CanAutoInstall);
    public bool ShowUpdateNotes => !UpdateBusy && !ShowUpdatePage;
    public bool ShowUpdateLater => !UpdateBusy;

    // ================================================================== About section

    public bool AutoCheckUpdates
    {
        get => _s.Update.AutoCheck;
        set
        {
            if (_s.Update.AutoCheck == value) return;
            _s.Update.AutoCheck = value;
            Save();
            OnPropertyChanged();
            if (value && Live) Later(() => _ = TickUpdatesAsync());
        }
    }

    public bool CanCheckUpdates => !_checking && !UpdateBusy; // mock mode has no checker, the click does nothing
    public string CheckUpdatesText => Loc.T(_checking ? "ui.update.checking" : "ui.update.checkNow");

    public string UpdateStatusText
    {
        get
        {
            if (_checking) return Loc.T("ui.update.checking");
            if (_release != null) return Loc.T("ui.update.statusAvailable", _release.DisplayVersion);
            if (_skippedRelease != null) return Loc.T("ui.update.statusSkipped", _skippedRelease.DisplayVersion);
            if (_checkStatus == UpdateCheckStatus.Failed) return Loc.T("ui.update.checkFailed", _checkError ?? "?");
            if (_checkStatus == UpdateCheckStatus.UpToDate) return Loc.T("ui.update.upToDate");
            return LastCheckedText;
        }
    }

    public Tone UpdateStatusTone =>
        _release != null ? Tone.Accent : _checkStatus == UpdateCheckStatus.Failed && !_checking ? Tone.Warn : Tone.Normal;

    public string LastCheckedText => _s.Update.LastCheckUtc is { } t
        ? Loc.T("ui.update.lastChecked", (t.Kind == DateTimeKind.Local ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime())
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
        : Loc.T("ui.update.neverChecked");

    private static readonly string[] UpdateProps =
    {
        nameof(UpdatePhase), nameof(ShowUpdateBanner), nameof(UpdateBusy), nameof(UpdateFailed), nameof(UpdateDownloading),
        nameof(UpdatePercent), nameof(UpdateTone), nameof(UpdateIcon), nameof(UpdateTitle), nameof(UpdateText),
        nameof(ShowUpdateInstall), nameof(UpdateInstallText), nameof(ShowUpdateSkip), nameof(ShowUpdatePage),
        nameof(ShowUpdateNotes), nameof(ShowUpdateLater), nameof(CanCheckUpdates), nameof(CheckUpdatesText),
        nameof(UpdateStatusText), nameof(UpdateStatusTone), nameof(LastCheckedText),
    };

    private void RaiseUpdate()
    {
        foreach (var p in UpdateProps) OnPropertyChanged(p);
    }

    private void SetUpdatePhase(UpdatePhase p)
    {
        _updatePhase = p;
        RaiseUpdate();
    }

    // ================================================================== checking

    /// <summary>Live mode: first check shortly after start, then every few minutes whether a check is due.</summary>
    private void StartUpdateChecks()
    {
        _checker = new UpdateChecker();
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateChecker.StartDelay };
        _updateTimer.Tick += (_, _) =>
        {
            if (_updateTimer == null) return;
            _updateTimer.Interval = UpdateTick;
            _ = TickUpdatesAsync();
        };
        _updateTimer.Start();
    }

    private void StopUpdateChecks()
    {
        _updateTimer?.Stop();
        _updateTimer = null;
        try { _updateCts?.Cancel(); } catch { }
        _checker?.Dispose();
        _checker = null;
    }

    private Task TickUpdatesAsync() => UpdateCheckDue(DateTime.UtcNow) ? CheckUpdatesAsync(manual: false) : Task.CompletedTask;

    private bool UpdateCheckDue(DateTime nowUtc)
    {
        if (!_s.Update.AutoCheck || _checking || UpdateBusy) return false;
        // a failed check (offline at logon, ...): retry soon a few times, then every few hours
        if (_checkFailedUtc is { } f)
            return nowUtc - f >= (_checkFailures < 3 ? QuickRetry : UpdateChecker.RetryAfterFailure);
        // once per run, then once a day
        return !_checkedThisRun || UpdateChecker.IsDue(_s.Update, nowUtc, null);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        var checker = _checker;
        if (checker == null || _checking || UpdateBusy) return;
        _checking = true;
        RaiseUpdate();
        UpdateCheckResult res;
        try { res = await checker.CheckAsync(_s.Update.SkippedVersion, manual, CancellationToken.None); }
        catch (Exception ex) { res = new UpdateCheckResult(UpdateCheckStatus.Failed, null, ex.Message, DateTime.UtcNow); }
        finally { _checking = false; }
        if (_checker == null) return; // detached meanwhile

        _checkStatus = res.Status;
        _checkError = res.Error;
        if (res.Status == UpdateCheckStatus.Failed)
        {
            _checkFailedUtc = DateTime.UtcNow;
            _checkFailures++;
            var line = Loc.T("ui.logline.updateCheckFailed", res.Error ?? "?");
            if (manual)
            {
                Logger.Warn(line);
                Toasts.Show(Loc.T("ui.update.checkFailed", res.Error ?? "?"), Tone.Bad);
            }
            else Logger.Debug(line);
            RaiseUpdate();
            return;
        }

        _checkFailedUtc = null;
        _checkFailures = 0;
        _checkedThisRun = true;
        _s.Update.LastCheckUtc = res.CheckedUtc;
        Save();

        switch (res.Status)
        {
            case UpdateCheckStatus.Available when res.Release != null:
                OfferRelease(res.Release, manual);
                break;
            case UpdateCheckStatus.Skipped when res.Release != null:
                _skippedRelease = res.Release;
                if (manual) Toasts.Show(Loc.T("ui.update.statusSkipped", res.Release.DisplayVersion));
                break;
            default:
                if (!UpdateBusy)
                {
                    _release = null;
                    _skippedRelease = null;
                    _updatePhase = UpdatePhase.None;
                }
                if (manual)
                {
                    Logger.Info(Loc.T("ui.logline.updateUpToDate", AppInfo.Version));
                    Toasts.Show(Loc.T("ui.update.upToDate"), Tone.Good);
                }
                break;
        }
        RaiseUpdate();
    }

    private void OfferRelease(ReleaseInfo r, bool manual)
    {
        bool isNew = _release == null || !UpdateVersion.Same(_release.Version, r.Version);
        if (UpdateBusy && !isNew) return;
        _release = r;
        _skippedRelease = null;
        if (manual) _updateHidden = false;
        if (isNew || _updatePhase == UpdatePhase.None)
        {
            _updatePhase = UpdatePhase.Available;
            _updateProblem = UpdateProblem.None;
            _updateError = null;
            Logger.Info(Loc.T("ui.logline.updateAvailable", r.DisplayVersion, AppInfo.Version));
        }
        if (!manual && !_host.IsWindowVisible && _balloonVersion != r.Version)
        {
            _balloonVersion = r.Version;
            _host.Balloon(new Notification(NotifyKind.Info, Loc.T("ui.update.trayTitle"), Loc.T("ui.update.trayText", r.DisplayVersion)));
        }
    }

    private void SkipUpdate()
    {
        var r = _release;
        if (r == null || UpdateBusy) return;
        _s.Update.SkippedVersion = r.Version;
        Save();
        Logger.Info(Loc.T("ui.logline.updateSkipped", r.DisplayVersion));
        _skippedRelease = r;
        _release = null;
        SetUpdatePhase(UpdatePhase.None);
    }

    /// <summary>The release page on GitHub (only pages of this repository are opened).</summary>
    private static string ReleasePageUrl(ReleaseInfo? r) =>
        r != null && r.HtmlUrl.StartsWith(RepoUrl + "/releases/", StringComparison.OrdinalIgnoreCase)
            ? r.HtmlUrl
            : RepoUrl + "/releases/latest";

    // ================================================================== installing

    private async Task InstallUpdateAsync()
    {
        var r = _release;
        if (r == null || UpdateBusy || !Live) return;
        if (!CanAutoInstall)
        {
            OpenUrl(ReleasePageUrl(r));
            return;
        }
        int online = _last?.Clients.Count(c => c.Online) ?? 0;
        if (online > 0)
        {
            var d = DialogVm.Custom(Loc.T("ui.update.confirmTitle"), Loc.T("ui.update.confirmText", online), DialogKind.Question, DialogVm.No,
                new DialogButtonVm(Loc.T("ui.update.install"), DialogVm.Yes, primary: true),
                new DialogButtonVm(Loc.T("dialog.cancel"), DialogVm.No));
            if (await Dialogs.ShowAsync(d) != DialogVm.Yes) return;
            if (_release != r || UpdateBusy) return;
        }

        _updateHidden = false;
        _updateError = null;
        _updateProblem = UpdateProblem.None;
        _updatePercent = 0;
        SetUpdatePhase(UpdatePhase.Downloading);
        var cts = new CancellationTokenSource();
        _updateCts = cts;
        UpdateStepResult res;
        using (var installer = new UpdateInstaller(_exePath))
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (_updatePhase != UpdatePhase.Downloading) return;
                var pct = Math.Floor(p.Fraction * 100);
                if (pct == _updatePercent) return;
                _updatePercent = pct;
                OnPropertyChanged(nameof(UpdatePercent));
                OnPropertyChanged(nameof(UpdateText));
            });
            try
            {
                res = await installer.DownloadAsync(r, progress, cts.Token);
                if (res.Ok && cts.IsCancellationRequested) res = new UpdateStepResult(UpdateProblem.Cancelled, null);
                if (res.Ok)
                {
                    SetUpdatePhase(UpdatePhase.Installing);
                    res = await Task.Run(() => installer.Apply(r, AppInfo.Version));
                }
            }
            catch (OperationCanceledException) { res = new UpdateStepResult(UpdateProblem.Cancelled, null); }
            catch (Exception ex) { res = new UpdateStepResult(UpdateProblem.Download, ex.Message); }
        }
        _updateCts = null;
        cts.Dispose();

        if (res.Ok)
        {
            // the new version is running and waits for this process to exit before it takes over (the hotspot stays on)
            await _host.ExitAsync(stopHotspot: false, ask: false);
            return;
        }
        if (res.Problem == UpdateProblem.Cancelled)
        {
            Toasts.Show(Loc.T("ui.update.cancelled"));
            SetUpdatePhase(UpdatePhase.Available);
            return;
        }
        _updateProblem = res.Problem;
        _updateError = ProblemText(res);
        Logger.Warn(Loc.T("ui.logline.updateFailed", _updateError));
        SetUpdatePhase(UpdatePhase.Failed);
    }

    private static string ProblemText(UpdateStepResult r) => r.Problem switch
    {
        UpdateProblem.NoChecksum => Loc.T("ui.update.err.noChecksum"),
        UpdateProblem.NoAsset => Loc.T("ui.update.err.noAsset"),
        UpdateProblem.NotWritable => Loc.T("ui.update.err.notWritable", r.Detail ?? "?"),
        UpdateProblem.Download => Loc.T("ui.update.err.download", r.Detail ?? "?"),
        UpdateProblem.SizeMismatch => Loc.T("ui.update.err.sizeMismatch", r.Detail ?? "?"),
        UpdateProblem.HashMismatch => Loc.T("ui.update.err.hashMismatch"),
        UpdateProblem.NotExecutable => Loc.T("ui.update.err.notExecutable"),
        UpdateProblem.Quarantined => Loc.T("ui.update.err.quarantined"),
        UpdateProblem.Replace => Loc.T("ui.update.err.replace", r.Detail ?? "?"),
        UpdateProblem.StartFailed => Loc.T("ui.update.err.startFailed", r.Detail ?? "?"),
        _ => r.Detail ?? r.Problem.ToString(),
    };

    /// <summary>Screenshots: show the banner for a made-up newer release.</summary>
    internal void SetMockUpdate(ReleaseInfo r, UpdatePhase phase = UpdatePhase.Available, double percent = 0, string? error = null)
    {
        _release = r;
        _updatePhase = phase;
        _updatePercent = percent;
        _updateError = error;
        _checkStatus = UpdateCheckStatus.Available;
        RaiseUpdate();
    }

    // ================================================================== Join by QR

    internal Task ShowQrAsync()
    {
        var ssid = _s.Ssid;
        if (string.IsNullOrWhiteSpace(ssid)) return Task.CompletedTask;
        QrJoinVm qr;
        try { qr = new QrJoinVm(ssid, _s.Password, _s.Auth); }
        catch (Exception ex)
        {
            Toasts.Show(Loc.T("ui.opFailed", ex.Message), Tone.Bad);
            return Task.CompletedTask;
        }
        bool on = _last?.Hotspot?.State == HotspotState.On;
        var notes = new List<string>();
        if (!on) notes.Add(Loc.T("ui.qr.off"));
        if (_s.Auth.Contains("WPA3", StringComparison.OrdinalIgnoreCase)) notes.Add(Loc.T("join.hintWpa3"));
        notes.Add(Loc.T("ui.qr.stillFails"));
        var d = new DialogVm(Loc.T("ui.qr.button"), Loc.T("ui.qr.text"), DialogKind.Info,
            new[] { new DialogButtonVm(Loc.T("dialog.close"), DialogVm.Close, primary: true) }, DialogVm.Close)
        {
            Qr = qr,
            Footnote = string.Join(Environment.NewLine, notes),
        };
        return Dialogs.ShowAsync(d);
    }
}

using System.Diagnostics;
using System.IO;
using VRAirBridge.Core;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.SystemSetup;

namespace VRAirBridge.App.Ui.ViewModels;

/// <summary>User actions (port of the MainForm button handlers). Every prompt is an in-app dialog.</summary>
public sealed partial class MainViewModel
{
    private bool Live => !_mock && _wd != null;

    // ================================================================== helpers

    private Task<string> Show(DialogVm d) => Dialogs.ShowAsync(d);

    private void Info(string text, DialogKind kind = DialogKind.Info) => _ = Show(DialogVm.Notice(text, kind));

    private async Task<bool> ConfirmAsync(string text, DialogKind kind = DialogKind.Question) =>
        await Show(DialogVm.Confirm(text, kind)) == DialogVm.Yes;

    private bool RequireAdmin(string what)
    {
        if (IsAdmin) return true;
        Info(Loc.T(CanElevate ? "ui.needAdminElevate" : "ui.needAdmin", what));
        return false;
    }

    private async Task RunUiOpAsync(Func<Task> body)
    {
        if (_uiBusy || !Live) return;
        _uiBusy = true;
        if (_last != null) ApplyStatus(_last);
        try { await body(); }
        catch (Exception ex)
        {
            Logger.Error(Loc.T("ui.logline.opFailed", ex.Message));
            Info(Loc.T("ui.opFailed", ex.Message), DialogKind.Error);
        }
        finally
        {
            _uiBusy = false;
            if (_last != null) ApplyStatus(_last);
        }
    }

    // ================================================================== hotspot

    private Task StartAsync() => RunUiOpAsync(() => _wd!.StartHotspotAsync());

    private Task StopAsync() => RunUiOpAsync(async () =>
    {
        await _wd!.StopHotspotAsync();
        Toasts.Show(Loc.T("ui.toast.stopped"));
    });

    private async Task ApplySettingsAsync()
    {
        if (!Live) return;
        var ssid = EditSsid.Trim();
        var pw = EditPassword;
        var err = AppSettings.Validate(ssid, pw);
        if (err != null)
        {
            Info(err, DialogKind.Warning);
            return;
        }
        var band = SelectedBand?.Value ?? "5GHz";
        var auth = SelectedAuth?.Value ?? "WPA2";
        if (band != "5GHz" && !await ConfirmAsync(Loc.T("ui.hs.confirmBand"), DialogKind.Warning)) return;
        if (auth == "WPA3" && !await ConfirmAsync(Loc.T("ui.hs.confirmWpa3"), DialogKind.Warning)) return;
        await RunUiOpAsync(async () =>
        {
            var e = await _wd!.ApplyHotspotSettingsAsync(ssid, pw, band, auth);
            if (e != null) Info(Loc.T("ui.hs.applyFailed", e), DialogKind.Warning);
            else
            {
                WindowsCurrentText = Loc.T("ui.hs.saved");
                Toasts.Show(Loc.T("ui.toast.applied"), Tone.Good);
                UpdateStartPage(_last);
            }
        });
    }

    // ================================================================== autostart

    private async Task SyncAutoStartAsync()
    {
        bool enabled;
        string? path;
        try
        {
            (enabled, path) = await Task.Run(() => (AutoStartManager.IsEnabled(out var p), p));
        }
        catch { return; }
        try
        {
            if (enabled && IsAdmin && path != null &&
                !string.Equals(Path.GetFullPath(path.Trim('"')), Path.GetFullPath(_exePath), StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info(Loc.T("ui.logline.autostartPath", path));
                await Task.Run(() => AutoStartManager.Enable(_exePath, out _));
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("ui.opFailed", ex.Message)); }
        if (_s.AutoStart != enabled)
        {
            _s.AutoStart = enabled;
            Save();
        }
        Quietly(() => OptAutoStart = enabled);
    }

    private async Task ToggleAutoStartAsync(bool enable)
    {
        if (!Live) return;
        if (!IsAdmin)
        {
            Info(Loc.T("ui.opt.autostartNeedAdmin"));
            Quietly(() => OptAutoStart = !enable);
            return;
        }
        string msg = "";
        bool ok = await Task.Run(() => enable ? AutoStartManager.Enable(_exePath, out msg) : AutoStartManager.Disable(out msg));
        Logger.Write(ok ? LogLevel.Ok : LogLevel.Error, msg);
        bool actual = await Task.Run(() => AutoStartManager.IsEnabled(out _));
        _s.AutoStart = actual;
        Save();
        Quietly(() => OptAutoStart = actual);
        if (!ok) Info(msg, DialogKind.Warning);
    }

    // ================================================================== internet

    private async Task ToggleInternetAsync(bool allow)
    {
        if (_internetBusy || !Live) return;
        _internetBusy = true;
        try
        {
            var (ok, msg) = await _wd!.SetInternetAllowedAsync(allow);
            Logger.Write(ok ? LogLevel.Ok : LogLevel.Warn, msg.Replace("\n", " "));
            bool visible = _host.IsWindowVisible;
            if (!ok && visible) Info(msg, DialogKind.Warning);
            else if (!visible) _host.Balloon(new Notification(ok ? NotifyKind.Info : NotifyKind.Warning, InternetStrings.StatusText(allow), msg));
            else Toasts.Show(InternetStrings.StatusText(allow), allow ? Tone.Good : Tone.Accent);
        }
        catch (Exception ex) { Logger.Error(Loc.T("ui.logline.internetFailed", ex.Message)); }
        finally
        {
            _internetBusy = false;
            Quietly(() => InternetAllowed = _s.Internet.Allow);
            _host.SetTrayInternet(_s.Internet.Allow);
        }
    }

    /// <summary>"--internet on|off" from a second instance (already on the UI thread).</summary>
    internal void ApplyInternetFromCli(bool allow)
    {
        Logger.Info(Loc.T("ui.logline.internetCli", allow ? "on" : "off"));
        _ = ToggleInternetAsync(allow);
    }

    // ================================================================== channel

    private async Task ScanChannelsAsync()
    {
        if (_scanning || !Live) return;
        _scanning = true;
        CanScan = false;
        ApplyChannel(_last ?? _wd!.Status);
        try
        {
            await Task.Run(() => _wd!.ScanChannelsAsync());
        }
        catch (Exception ex) { Logger.Warn(ChannelStrings.ScanFailedPrefix + ex.Message); }
        finally
        {
            _scanning = false;
            CanScan = true;
            ApplyChannel(_wd!.Status);
        }
    }

    private async Task InitialScanAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (_exiting || ChannelInfo.Last != null) return;
            await ScanChannelsAsync();
        }
        catch { }
    }

    private async Task PinChangedAsync(PinItemVm pc)
    {
        var cur = (_s.Channel.PinSsid ?? "").Trim();
        if (pc.Ssid == cur) return;
        void Undo()
        {
            _pinItemsKey = "";
            RebuildPinItems(_last?.Channel);
        }
        if (!Live) { Undo(); return; }
        if (pc.Ssid.Length > 0)
        {
            if (pc.Candidate is { Dfs: true } or { Is24GHz: true })
            {
                var c = pc.Candidate!;
                Undo();
                Info(string.Format(c.Dfs ? ChannelStrings.PinPickDfs : ChannelStrings.PinPick24, c.Ssid, c.Channel));
                return;
            }
            if (!RequireAdmin(ChannelStrings.PinWhat)) { Undo(); return; }
            if (pc.Candidate is { HasProfile: false } && !await ConfirmAsync(string.Format(ChannelStrings.PinPickNoProfile, pc.Ssid)))
            {
                Undo();
                return;
            }
        }
        await RunUiOpAsync(async () =>
        {
            var msg = await _wd!.SetPinSsidAsync(pc.Ssid);
            Logger.Info(ChannelStrings.NotifyTitle + Loc.T("common.colon") + msg);
            if (msg == ChannelStrings.PinNextStart) Info(msg);
        });
        Undo();
    }

    // ================================================================== driver

    /// <summary>Called when the main window becomes visible (a pending driver prompt is shown then).</summary>
    internal void OnWindowShown() => Later(() => TryPromptDriver(_last?.Driver));

    /// <summary>D-Link plugged in without a driver: ask "install now / later" once per plug-in (when the window is visible).</summary>
    private void TryPromptDriver(DriverState? current)
    {
        if (!_driverPromptPending) return;
        if (current != null && current.Kind != DriverStateKind.PresentNoDriver)
        {
            _driverPromptPending = false;
            return;
        }
        if (_driverPromptShowing || !_host.IsWindowVisible || _uiBusy || _exiting || _mock) return;
        _driverPromptPending = false;
        _driverPromptShowing = true;
        _ = ShowDriverPromptAsync();
    }

    private async Task ShowDriverPromptAsync()
    {
        try
        {
            var d = new DialogVm(DriverStrings.PromptTitle, DriverStrings.PromptHeading + "\n\n" + DriverStrings.PromptText, DialogKind.Warning,
                new[]
                {
                    new DialogButtonVm(DriverStrings.PromptInstallNow, "install", primary: true),
                    new DialogButtonVm(DriverStrings.PromptLater, "later"),
                }, "later")
            {
                Footnote = IsAdmin ? null : DriverStrings.NeedAdmin,
            };
            if (await Show(d) == "install") _ = InstallDriverAsync(fromPrompt: true);
        }
        catch (Exception ex) { Logger.Debug(Loc.T("ui.logline.driverPromptError", ex.Message)); }
        finally { _driverPromptShowing = false; }
    }

    private async Task InstallDriverAsync(bool fromPrompt)
    {
        if (!Live) return;
        if (!RequireAdmin(DriverStrings.ButtonInstall)) return;
        var cur = _last?.Driver;
        if (!fromPrompt && cur?.Kind == DriverStateKind.DriverOk && !await ConfirmAsync(DriverStrings.ReinstallConfirm)) return;
        if (!fromPrompt && cur?.Kind == DriverStateKind.DriverOtherVersion &&
            !await ConfirmAsync(string.Format(DriverStrings.OtherVersionConfirm, cur.VersionText), DialogKind.Warning)) return;
        await RunUiOpAsync(async () =>
        {
            var progress = new Progress<string>(m => Detail = "⏳ " + m);
            var pkg = DriverPackage.FromAssembly(typeof(Program).Assembly);
            var r = await Task.Run(() => _wd!.InstallDriverAsync(pkg, progress));
            var lines = new List<string> { (r.Ok ? "✔ " : "✖ ") + r.Message };
            lines.AddRange(r.Steps.Where(s => s != r.Message));
            if (!r.Ok && r.PnpUtil != null && !string.IsNullOrWhiteSpace(r.PnpUtil.Output) && !r.Message.Contains(r.PnpUtil.Output.Trim()))
            {
                lines.Add(Loc.T("ui.logline.pnputil"));
                lines.AddRange(r.PnpUtil.Output.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(l => "    " + l.Trim()));
            }
            _ = Show(DialogVm.Report(DriverStrings.ResultTitle, lines, DriverStrings.ResultFooter, r.Ok ? DialogKind.Success : DialogKind.Warning));
        });
    }

    // ================================================================== maintenance

    private async Task OptimizeAsync()
    {
        if (!Live || !RequireAdmin(Loc.T("ui.what.optimize"))) return;
        if (!await ConfirmAsync(Loc.T("ui.maint.optimizeConfirm"))) return;
        await RunUiOpAsync(async () =>
        {
            var res = await Task.Run(() => _wd!.OptimizeAsync(Dialogs.ConfirmBlocking));
            var lines = res.Select(r => (r.Level switch
            {
                LogLevel.Ok => "✔ ",
                LogLevel.Warn => "⚠ ",
                LogLevel.Error => "✖ ",
                _ => "",
            }) + r.Text);
            var kind = res.Any(r => r.Level == LogLevel.Error) ? DialogKind.Warning : DialogKind.Success;
            _ = Show(DialogVm.Report(Loc.T("ui.maint.optimizeResult"), lines, null, kind));
        });
    }

    private async Task RepairAsync()
    {
        if (!Live || !RequireAdmin(Loc.T("ui.what.repair"))) return;
        if (!await ConfirmAsync(Loc.T("ui.maint.repairConfirm"))) return;
        await RunUiOpAsync(async () =>
        {
            var res = await Task.Run(() => _wd!.RepairNetworkAsync(Dialogs.ConfirmBlocking));
            _ = Show(DialogVm.Report(Loc.T("ui.maint.repairResult"), res, Loc.T("ui.maint.repairFooter")));
        });
    }

    private Task BeaconAsync() => RunUiOpAsync(async () =>
    {
        await _wd!.CheckBeaconNowAsync();
        var b = _wd.LastBeacon;
        var lines = new List<string>();
        if (b == null) lines.Add(_wd.LastBeaconNote ?? Loc.T("ui.beacon.none"));
        else
        {
            var i = b.Info;
            lines.Add(Loc.T("ui.beacon.ssid", b.Ssid, b.Bssid));
            lines.Add(Loc.T("ui.beacon.channel", i.Channel, i.FreqMHz, i.FreqMHz > 5000 ? "" : Loc.T("ui.beacon.not5")));
            lines.Add(Loc.T("ui.beacon.width", i.WidthMHz, i.WidthMHz >= 80 ? "" : Loc.T("ui.beacon.not80")));
            lines.Add(Loc.T("ui.beacon.phy", i.Phy, i.Phy == "802.11ax" ? "" : Loc.T("ui.beacon.notAx")));
            lines.Add(Loc.T("ui.beacon.streams", i.Streams, i.Streams >= 2 ? "" : Loc.T("ui.beacon.not2")));
            lines.Add(Loc.T("ui.beacon.full", Loc.T(i.IsFull ? "ui.beacon.fullYes" : "ui.beacon.fullNo")));
            lines.Add(Loc.T("ui.beacon.signal", b.Rssi, b.ScannerName, b.Time));
            lines.Add(b.SameChannelOthers.Count == 0
                ? Loc.T("ui.beacon.noOthers")
                : Loc.T("ui.beacon.others", b.SameChannelOthers.Count, string.Join(Loc.T("common.enumSep"), b.SameChannelOthers.Take(12))));
        }
        _ = Show(DialogVm.Report(Loc.T("ui.beacon.title"), lines, Loc.T("ui.beacon.footer")));
    });

    private Task CopyDiagAsync() => RunUiOpAsync(async () =>
    {
        var text = await Task.Run(() => Diagnostics.Collect(_s, _exePath));
        _host.CopyText(text);
        var dir = _store?.DataDirectory ?? _wd!.DataDirectory;
        var path = Path.Combine(dir, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try { File.WriteAllText(path, text, new System.Text.UTF8Encoding(true)); }
        catch { path = ""; }
        Info(path != "" ? Loc.T("ui.maint.diagSaved", path) : Loc.T("ui.maint.diagCopied"), DialogKind.Success);
    });

    private async Task RevertAsync()
    {
        if (!Live || !RequireAdmin(Loc.T("ui.what.revert"))) return;
        if (!await ConfirmAsync(Loc.T("ui.maint.revertConfirm"), DialogKind.Warning)) return;
        await RunUiOpAsync(async () =>
        {
            var res = await _wd!.RevertAsync();
            Quietly(() => OptAutoStart = false);
            _ = Show(DialogVm.Report(Loc.T("ui.maint.revertResult"), res, Loc.T("ui.maint.revertFooter") + "\n\n" + Loc.T("ui.maint.revertDone")));
        });
    }

    private async Task MetaCheckAsync()
    {
        if (_metaChecking || _mock) return;
        MetaChecking = true;
        try
        {
            var rep = await Task.Run(MetaLeftovers.Scan);
            MetaText = rep.Any ? string.Join("\n", rep.DescribeLines()) : MetaStrings.NoneFound;
        }
        catch (Exception ex) { MetaText = Loc.T("ui.opFailed", ex.Message); }
        finally { MetaChecking = false; }
    }

    private void OpenLogs()
    {
        if (_mock) return;
        try
        {
            var dir = Logger.LogDirectory ?? _store?.LogDirectory ?? _wd!.DataDirectory;
            var today = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(today)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{today}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Info(Loc.T("ui.maint.logsFailed", ex.Message), DialogKind.Warning); }
    }

    private void OpenDataDir()
    {
        if (_mock) return;
        try
        {
            var dir = _store?.DataDirectory ?? _wd!.DataDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Info(Loc.T("ui.opFailed", ex.Message), DialogKind.Warning); }
    }

    private async Task ElevateAsync()
    {
        if (!Live) return;
        try
        {
            Process.Start(new ProcessStartInfo(_exePath, $"--relaunch-from {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex)
        {
            Logger.Warn(Loc.T("ui.logline.elevateCancelled", ex.Message));
            return;
        }
        Logger.Info(Loc.T("ui.logline.elevating"));
        await _host.ExitAsync(stopHotspot: false, ask: false);
    }

    // ================================================================== log / devices

    private void ClearLog()
    {
        _logClearedAt = DateTime.Now;
        LogLines = new System.Collections.ObjectModel.ObservableCollection<LogLineVm>();
        OnPropertyChanged(nameof(HasLog));
        Toasts.Show(Loc.T("ui.toast.logCleared"));
    }

    private void CopyLog(System.Collections.IList? selected)
    {
        var lines = selected is { Count: > 0 }
            ? selected.OfType<LogLineVm>().OrderBy(l => _logLines.IndexOf(l)).Select(l => l.Text)
            : _logLines.Select(l => l.Text);
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        _host.CopyText(text);
        Toasts.Show(Loc.T("ui.log.copied"), Tone.Good);
    }

    private void CopyDevice()
    {
        var rows = SelectedDevice != null ? new[] { SelectedDevice } : Devices.ToArray();
        if (rows.Length == 0) return;
        _host.CopyText(string.Join(Environment.NewLine, rows.Select(r => r.CopyText)));
        Toasts.Show(Loc.T("ui.devices.copied"), Tone.Good);
    }
}

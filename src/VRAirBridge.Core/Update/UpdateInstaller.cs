using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.Update;

/// <summary>Why an update step did not work.</summary>
public enum UpdateProblem
{
    None,
    /// <summary>The release has no program file attached.</summary>
    NoAsset,
    /// <summary>Neither a GitHub asset digest nor a SHA-256 in the release notes: cannot verify, so no automatic install.</summary>
    NoChecksum,
    /// <summary>The folder of the program file is not writable.</summary>
    NotWritable,
    /// <summary>The download failed (network, HTTP error, stalled).</summary>
    Download,
    /// <summary>The download has another size than the release says.</summary>
    SizeMismatch,
    /// <summary>The download has another SHA-256 than the release says.</summary>
    HashMismatch,
    /// <summary>The download is not a Windows program.</summary>
    NotExecutable,
    /// <summary>The downloaded file vanished, cannot be read or was blocked from running: most likely antivirus.</summary>
    Quarantined,
    /// <summary>The program file could not be replaced (nothing was changed).</summary>
    Replace,
    /// <summary>The new version did not start (the previous version was put back).</summary>
    StartFailed,
    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}

public sealed record UpdateProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

public sealed record UpdateStepResult(UpdateProblem Problem, string? Detail = null)
{
    public static readonly UpdateStepResult Success = new(UpdateProblem.None);
    public bool Ok => Problem == UpdateProblem.None;
}

/// <summary>Result of starting the new version.</summary>
public sealed record LaunchResult(bool Ok, string? Error = null, bool Blocked = false);

/// <summary>
/// Downloads a release next to the running program, verifies size and SHA-256, swaps the files
/// (exe to ".old", ".download" to exe; Windows allows renaming a running exe) and starts the new version, which
/// signals that it runs and then waits for this process to exit. Anything that goes wrong after the swap puts the
/// previous version back. The new version deletes the leftovers (<see cref="CleanupLeftoversAsync"/>).
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    public const string UpdatedFromArg = "--updated-from";
    public const string RelaunchFromArg = "--relaunch-from";

    /// <summary>No data for this long: the download counts as failed.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for the new version to say that it runs.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private const int ErrorVirusInfected = 225, ErrorVirusDeleted = 226, ErrorElevationRequired = 740;

    private readonly HttpClient _http;

    public UpdateInstaller(string exePath, HttpMessageHandler? handler = null)
    {
        ExePath = Path.GetFullPath(exePath);
        _http = handler == null ? new HttpClient(UpdateHttp.CreateHandler(), disposeHandler: true) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string ExePath { get; }
    public string DownloadPath => ExePath + ".download";
    public string OldPath => ExePath + ".old";

    /// <summary>Replaces <see cref="Launch"/> (tests): (exe path, arguments) gives the launch result.</summary>
    public Func<string, string, LaunchResult>? Launcher { get; init; }

    /// <summary>Whether a release can be installed automatically.</summary>
    public static UpdateProblem CanInstall(ReleaseInfo r) =>
        r.Exe == null ? UpdateProblem.NoAsset : r.ExpectedSha256 == null ? UpdateProblem.NoChecksum : UpdateProblem.None;

    // ================================================================== download

    public async Task<UpdateStepResult> DownloadAsync(ReleaseInfo r, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var pre = CanInstall(r);
        if (pre != UpdateProblem.None) return new(pre);
        var asset = r.Exe!;
        var expected = r.ExpectedSha256!;
        var dir = Path.GetDirectoryName(ExePath)!;
        if (!IsWritable(dir)) return new(UpdateProblem.NotWritable, dir);

        TryDelete(DownloadPath);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            req.Headers.UserAgent.ParseAdd(AppInfo.UserAgent);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var head = CancellationTokenSource.CreateLinkedTokenSource(ct);
            head.CancelAfter(StallTimeout);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, head.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new(UpdateProblem.Download, UpdateHttp.StatusText(resp));
            long total = asset.Size > 0 ? asset.Size : resp.Content.Headers.ContentLength ?? 0;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(DownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                var sw = Stopwatch.StartNew();
                progress?.Report(new UpdateProgress(0, total));
                while (true)
                {
                    int n;
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        stall.CancelAfter(StallTimeout);
                        n = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false);
                    }
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buf, 0, n);
                    received += n;
                    if (asset.Size > 0 && received > asset.Size) break;
                    if (sw.ElapsedMilliseconds >= 120)
                    {
                        sw.Restart();
                        progress?.Report(new UpdateProgress(received, total));
                    }
                }
            }
            progress?.Report(new UpdateProgress(received, total));

            if (asset.Size > 0 && received != asset.Size)
                return Discard(UpdateProblem.SizeMismatch, Loc.T("update.sizeDetail", received, asset.Size));
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != expected) return Discard(UpdateProblem.HashMismatch, actual);
            if (!File.Exists(DownloadPath)) return new(UpdateProblem.Quarantined);
            if (!LooksLikeExe(DownloadPath)) return Discard(UpdateProblem.NotExecutable);
            return UpdateStepResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Discard(UpdateProblem.Cancelled); }
        catch (OperationCanceledException) { return Discard(UpdateProblem.Download, Loc.T("update.stalled")); }
        catch (UnauthorizedAccessException ex) { return Discard(UpdateProblem.Quarantined, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return Discard(UpdateProblem.Download, UpdateHttp.Message(ex)); }
    }

    private UpdateStepResult Discard(UpdateProblem p, string? detail = null)
    {
        TryDelete(DownloadPath);
        return new(p, detail);
    }

    // ================================================================== apply

    /// <summary>
    /// Swaps the program file and starts the new version. On success the caller must exit at once: the new process
    /// waits for this one before it takes over the hotspot.
    /// </summary>
    public UpdateStepResult Apply(ReleaseInfo r, string currentVersion)
    {
        var pre = CanInstall(r);
        if (pre != UpdateProblem.None) return new(pre);

        // 1) the verified download must still be there and unchanged (antivirus may delete, lock or alter it)
        if (!File.Exists(DownloadPath)) return new(UpdateProblem.Quarantined);
        try
        {
            if (!MatchesHash(DownloadPath, r.Exe!.Size, r.ExpectedSha256!)) return Discard(UpdateProblem.HashMismatch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(UpdateProblem.Quarantined, ex.Message); }

        // 2) swap: exe to .old, .download to exe
        TryDelete(OldPath);
        if (File.Exists(OldPath)) return new(UpdateProblem.Replace, OldPath);
        try { File.Move(ExePath, OldPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(UpdateProblem.Replace, ex.Message); }
        try { File.Move(DownloadPath, ExePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback();
            return new(UpdateProblem.Replace, ex.Message);
        }

        // 3) start the new version and wait until it says it runs
        var args = RelaunchFromArg + " " + Environment.ProcessId + " " + UpdatedFromArg + " " + currentVersion;
        LaunchResult launched;
        try { launched = (Launcher ?? Launch)(ExePath, args); }
        catch (Exception ex) { launched = new LaunchResult(false, ex.Message); }
        if (!launched.Ok)
        {
            Rollback();
            return new(launched.Blocked ? UpdateProblem.Quarantined : UpdateProblem.StartFailed, launched.Error);
        }
        Logger.Info(Loc.T("update.logline.restarting", r.DisplayVersion));
        return UpdateStepResult.Success;
    }

    /// <summary>Puts the previous program file back (the new one is deleted).</summary>
    private void Rollback()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(OldPath)) return;
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(OldPath, ExePath);
                TryDelete(DownloadPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the new process may still hold its image file for a moment after it exited
                if (attempt == 9) Logger.Error(Loc.T("update.logline.rollbackFailed", ex.Message, OldPath));
                else Thread.Sleep(300);
            }
        }
    }

    /// <summary>Starts the new version; Ok once it signalled that it runs (or is still running after the timeout).</summary>
    public static LaunchResult Launch(string exePath, string arguments)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(Environment.ProcessId));
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            // this process is not elevated (debug runs): ask for elevation like "restart as administrator" does
            try { p = Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = true, Verb = "runas" }); }
            catch (Exception ex2) { return new LaunchResult(false, ex2.Message); }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorVirusInfected or ErrorVirusDeleted)
        {
            return new LaunchResult(false, ex.Message, Blocked: true);
        }
        catch (Exception ex) { return new LaunchResult(false, ex.Message); }
        if (p == null) return new LaunchResult(false, "Process.Start returned null");

        using (p)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < ReadyTimeout)
            {
                if (ready.WaitOne(250)) return new LaunchResult(true);
                bool exited;
                try { exited = p.HasExited; } catch { exited = false; }
                if (exited)
                {
                    int code = 0;
                    try { code = p.ExitCode; } catch { }
                    return new LaunchResult(false, Loc.T("update.exitedEarly", code));
                }
            }
            return new LaunchResult(true); // slow start but alive: let it take over
        }
    }

    // ================================================================== new process side

    public static string ReadyEventName(int oldPid) => @"Local\VRAirBridgeHotspot.UpdateReady." + oldPid;

    /// <summary>Called early by the new version: tells the old one (waiting in <see cref="Launch"/>) that it runs.</summary>
    public static void SignalReady(int oldPid)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ReadyEventName(oldPid), out var h))
                using (h) h.Set();
        }
        catch { }
    }

    /// <summary>Deletes "*.exe.old" / "*.exe.download" left by an update. Retries for a while (the old process may still be exiting).</summary>
    public static async Task CleanupLeftoversAsync(string exePath, TimeSpan? maxWait = null)
    {
        var paths = new[] { exePath + ".old", exePath + ".download" };
        var until = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            foreach (var p in paths) TryDelete(p);
            if (!paths.Any(File.Exists) || DateTime.UtcNow >= until) return;
            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    // ================================================================== helpers

    public static bool MatchesHash(string path, long size, string sha256)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (size > 0 && fs.Length != size) return false;
        var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return hash == sha256.ToLowerInvariant();
    }

    /// <summary>Starts with the "MZ" DOS header of a Windows program.</summary>
    public static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".update-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => _http.Dispose();
}

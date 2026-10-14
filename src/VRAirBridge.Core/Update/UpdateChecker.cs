using System.Net;
using VRAirBridge.Core.Settings;

namespace VRAirBridge.Core.Update;

public enum UpdateCheckStatus
{
    /// <summary>The latest release is not newer than this version (or there is no release yet).</summary>
    UpToDate,
    /// <summary>A newer release exists and should be offered.</summary>
    Available,
    /// <summary>A newer release exists, but the user skipped exactly that version (automatic checks only).</summary>
    Skipped,
    /// <summary>The check failed (offline, GitHub unreachable, rate limit, unexpected answer).</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, string? Error, DateTime CheckedUtc)
{
    public bool IsAvailable => Status == UpdateCheckStatus.Available;
}

/// <summary>
/// Asks GitHub for the latest release of the app. Never throws for network or format problems (they become
/// <see cref="UpdateCheckStatus.Failed"/>); only a cancellation requested by the caller propagates.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>First automatic check after the app started (the hotspot start comes first).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
    /// <summary>Automatic checks at most this often (after a successful check).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    /// <summary>After a failed automatic check, wait this long before trying again.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(3);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _repo;

    public UpdateChecker(string? currentVersion = null, string repo = AppInfo.GitHubRepo, HttpMessageHandler? handler = null)
    {
        CurrentVersion = currentVersion ?? AppInfo.Version;
        _repo = repo;
        _http = handler == null ? new HttpClient(UpdateHttp.CreateHandler(), disposeHandler: true) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string CurrentVersion { get; }

    public static string LatestUrl(string repo) => "https://api.github.com/repos/" + repo + "/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(string? skippedVersion, bool manual, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        UpdateCheckResult Failed(string error) => new(UpdateCheckStatus.Failed, null, error, now);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl(_repo));
            req.Headers.UserAgent.ParseAdd(AppInfo.UserAgent);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            // 404: the repository has no published release yet
            if (resp.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckStatus.UpToDate, null, null, now);
            if (!resp.IsSuccessStatusCode)
                return Failed(UpdateHttp.StatusText(resp));
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var rel = ReleaseInfo.Parse(json);
            return new(Evaluate(rel, CurrentVersion, skippedVersion, manual), rel, null, now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failed(Loc.T("update.timeout")); }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or IOException or InvalidOperationException)
        {
            return Failed(UpdateHttp.Message(ex));
        }
    }

    /// <summary>Whether a release should be offered to this version.</summary>
    public static UpdateCheckStatus Evaluate(ReleaseInfo r, string currentVersion, string? skippedVersion, bool manual)
    {
        if (r.Draft || r.Prerelease || !UpdateVersion.IsNewer(r.Version, currentVersion)) return UpdateCheckStatus.UpToDate;
        if (!manual && UpdateVersion.Same(r.Version, skippedVersion)) return UpdateCheckStatus.Skipped;
        return UpdateCheckStatus.Available;
    }

    /// <summary>An automatic check is due: enabled, and the last successful check is older than <see cref="Interval"/>.</summary>
    public static bool IsDue(UpdateSettings s, DateTime nowUtc, DateTime? lastFailureUtc = null)
    {
        if (!s.AutoCheck) return false;
        if (lastFailureUtc is { } f && nowUtc >= f && nowUtc - f < RetryAfterFailure) return false;
        if (s.LastCheckUtc is not { } last) return true;
        var lastUtc = last.Kind == DateTimeKind.Local ? last.ToUniversalTime() : last;
        if (lastUtc > nowUtc + TimeSpan.FromHours(1)) return true; // the clock went back: check instead of waiting days
        return nowUtc - lastUtc >= Interval;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>HTTP pieces shared by the checker and the installer.</summary>
internal static class UpdateHttp
{
    /// <summary>System proxy, compressed answers, a connect timeout (GitHub can be slow or blocked on some networks).</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static string StatusText(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        // GitHub answers 403/429 with x-ratelimit-remaining: 0 when the hourly limit for this IP is used up
        if ((code == 403 || code == 429) && resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && rem.FirstOrDefault() == "0")
            return Loc.T("update.rateLimited");
        return "HTTP " + code + (string.IsNullOrWhiteSpace(resp.ReasonPhrase) ? "" : " " + resp.ReasonPhrase);
    }

    public static string Message(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null && e is HttpRequestException) e = e.InnerException;
        return e.Message;
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using VRAirBridge.Core;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Update;
using Xunit;

namespace VRAirBridge.Tests;

/// <summary>Answers HTTP requests from a delegate and records them (no network).</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
    public readonly List<HttpRequestMessage> Requests = new();

    public FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

    public FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : this((r, _) => Task.FromResult(answer(r))) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return _answer(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("1.0.2", "1.0.1", 1)]
    [InlineData("v1.0.10", "1.0.9", 1)]
    [InlineData("1.0.2", "1.0.2-beta.1", 1)]
    [InlineData("1.0.2-beta.2", "1.0.2-beta.10", -1)]
    [InlineData("1.0.2-alpha", "1.0.2-beta", -1)]
    [InlineData("1.0.2-beta", "1.0.2-beta.1", -1)]
    [InlineData("1.0.2-1", "1.0.2-rc", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    [InlineData("1.0.2+abc123", "V1.0.2", 0)]
    [InlineData("2.0.0", "1.99.99", 1)]
    public void Compare(string a, string b, int expected)
    {
        Assert.True(UpdateVersion.TryParse(a, out var x));
        Assert.True(UpdateVersion.TryParse(b, out var y));
        Assert.Equal(expected, Math.Sign(x.CompareTo(y)));
        Assert.Equal(-expected, Math.Sign(y.CompareTo(x)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1..2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2-")]
    [InlineData("1.2-beta..1")]
    [InlineData("-1.2")]
    [InlineData("1.-2")]
    public void Invalid(string? text) => Assert.False(UpdateVersion.TryParse(text, out _));

    [Fact]
    public void IsNewer_And_Same()
    {
        Assert.True(UpdateVersion.IsNewer("1.0.3", "1.0.2"));
        Assert.False(UpdateVersion.IsNewer("1.0.2", "1.0.2"));
        Assert.False(UpdateVersion.IsNewer("1.0.1", "1.0.2"));
        Assert.False(UpdateVersion.IsNewer("garbage", "1.0.2"));
        Assert.True(UpdateVersion.IsNewer("1.0.0", "garbage"));
        Assert.True(UpdateVersion.Same("v1.0.2", "1.0.2+abc"));
        Assert.False(UpdateVersion.Same("1.0.2", ""));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.True(UpdateVersion.TryParse("v1.2.3-beta.1", out var a));
        Assert.Equal("1.2.3-beta.1", a.ToString());
        Assert.True(UpdateVersion.TryParse("1.2.3.4", out var b));
        Assert.Equal("1.2.3.4", b.ToString());
        Assert.True(UpdateVersion.TryParse("1.2", out var c));
        Assert.Equal("1.2.0", c.ToString());
    }

    [Fact]
    public void AppVersion_IsValid()
    {
        Assert.True(UpdateVersion.TryParse(AppInfo.Version, out _));
        Assert.Equal("v" + AppInfo.Version, AppInfo.DisplayVersion);
        Assert.DoesNotContain("+", AppInfo.Version);
        Assert.Equal("1.0.2", AppInfo.StripMetadata("v1.0.2+abc"));
        Assert.Equal("", AppInfo.StripMetadata(null));
    }
}

public class ReleaseInfoTests
{
    internal const string Sha = "3f1b8a0c9d2e4f5061728394a5b6c7d8e9f00112233445566778899aabbccdde";

    internal static string ReleaseJson(string tag = "v1.0.9", string? digest = "sha256:" + Sha, string body = "Fixes.",
        string assetName = AppInfo.ExeName, string url = "https://github.com/x/y/releases/download/v1.0.9/VRAirBridgeHotspot.exe",
        bool prerelease = false, bool draft = false, long size = 1234)
    {
        static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var dig = digest == null ? "" : ",\"digest\":" + Q(digest);
        return "{\"tag_name\":" + Q(tag) + ",\"name\":\"\",\"body\":" + Q(body) +
               ",\"html_url\":\"https://github.com/AlanBacker/VR-AirBridge-Hotspot/releases/tag/" + tag + "\"" +
               ",\"published_at\":\"2026-10-13T10:00:00Z\",\"prerelease\":" + (prerelease ? "true" : "false") +
               ",\"draft\":" + (draft ? "true" : "false") +
               ",\"assets\":[{\"name\":\"notes.txt\",\"size\":5,\"browser_download_url\":\"https://example.com/notes.txt\"}," +
               "{\"name\":" + Q(assetName) + ",\"size\":" + size + ",\"browser_download_url\":" + Q(url) + dig + "}]}";
    }

    [Fact]
    public void Parse_FullRelease()
    {
        var r = ReleaseInfo.Parse(ReleaseJson(digest: "sha256:" + Sha.ToUpperInvariant()));
        Assert.Equal("v1.0.9", r.Tag);
        Assert.Equal("1.0.9", r.Version);
        Assert.Equal("v1.0.9", r.Name); // empty name falls back to the tag
        Assert.Equal("v1.0.9", r.DisplayVersion);
        Assert.Equal(new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc), r.PublishedUtc);
        Assert.False(r.Prerelease);
        Assert.NotNull(r.Exe);
        Assert.Equal(AppInfo.ExeName, r.Exe!.Name);
        Assert.Equal(1234, r.Exe.Size);
        Assert.Equal(Sha, r.ExpectedSha256);
        Assert.Equal(UpdateProblem.None, UpdateInstaller.CanInstall(r));
    }

    [Fact]
    public void Sha_FromBody_WhenNoDigest()
    {
        var body = "## Changes\n- iPhone join fix\n\nSHA-256 (VRAirBridgeHotspot.exe): " + Sha.ToUpperInvariant() + "\r\n";
        var r = ReleaseInfo.Parse(ReleaseJson(digest: null, body: body));
        Assert.Null(r.Exe!.Sha256);
        Assert.Equal(Sha, r.ExpectedSha256);
    }

    [Fact]
    public void ShaFromBody_PicksTheExeLine()
    {
        var other = new string('a', 64);
        var body = "other.zip " + other + "\nVRAirBridgeHotspot.exe " + Sha + "\n";
        Assert.Equal(Sha, ReleaseInfo.ShaFromBody(body, AppInfo.ExeName));
        // two different hashes and none on the exe line: unknown
        Assert.Null(ReleaseInfo.ShaFromBody("a.zip " + other + "\nb.zip " + Sha, AppInfo.ExeName));
        // a single hash anywhere is enough
        Assert.Equal(Sha, ReleaseInfo.ShaFromBody("checksum: " + Sha, AppInfo.ExeName));
        // 65 hex digits is not a SHA-256
        Assert.Null(ReleaseInfo.ShaFromBody("x " + Sha + "0", AppInfo.ExeName));
        Assert.Null(ReleaseInfo.ShaFromBody(null, AppInfo.ExeName));
    }

    [Fact]
    public void NoChecksum_MeansNoAutomaticInstall()
    {
        var r = ReleaseInfo.Parse(ReleaseJson(digest: null, body: "No hash here."));
        Assert.Null(r.ExpectedSha256);
        Assert.Equal(UpdateProblem.NoChecksum, UpdateInstaller.CanInstall(r));
    }

    [Fact]
    public void Asset_Selection()
    {
        // a single exe with another name is still taken
        var r = ReleaseInfo.Parse(ReleaseJson(assetName: "VRAirBridgeHotspot-1.0.9.exe"));
        Assert.Equal("VRAirBridgeHotspot-1.0.9.exe", r.Exe!.Name);
        // plain http download URLs are ignored
        var http = ReleaseInfo.Parse(ReleaseJson(url: "http://github.com/x/y/VRAirBridgeHotspot.exe"));
        Assert.Null(http.Exe);
        Assert.Equal(UpdateProblem.NoAsset, UpdateInstaller.CanInstall(http));
        // no exe at all
        var zip = ReleaseInfo.Parse(ReleaseJson(assetName: "source.zip"));
        Assert.Null(zip.Exe);
        // the setup is never taken as the program file
        var setupOnly = ReleaseInfo.Parse(ReleaseJson(assetName: AppInfo.SetupName));
        Assert.Null(setupOnly.Exe);
    }

    [Fact]
    public void Asset_Selection_WithSetup()
    {
        // the setup listed first, then the portable exe: self-update takes the portable exe
        var json = ReleaseJson().Replace("{\"name\":\"notes.txt\"",
            "{\"name\":\"" + AppInfo.SetupName + "\",\"size\":99,\"browser_download_url\":\"https://example.com/" + AppInfo.SetupName + "\"},{\"name\":\"notes.txt\"");
        Assert.Contains(AppInfo.SetupName, json);
        var r = ReleaseInfo.Parse(json);
        Assert.Equal(AppInfo.ExeName, r.Exe!.Name);
        Assert.Equal(1234, r.Exe.Size);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"latest\"}")]
    public void Parse_Rejects(string json) => Assert.Throws<FormatException>(() => ReleaseInfo.Parse(json));

    [Theory]
    [InlineData("sha256:" + Sha, Sha)]
    [InlineData("  SHA256:" + Sha + " ", Sha)]
    [InlineData("sha512:" + Sha, null)]
    [InlineData("sha256:abc", null)]
    [InlineData("sha256:zz1b8a0c9d2e4f5061728394a5b6c7d8e9f00112233445566778899aabbccdde", null)]
    [InlineData(null, null)]
    public void ParseDigest(string? digest, string? expected) => Assert.Equal(expected, ReleaseInfo.ParseDigest(digest));
}

public class UpdateCheckerTests
{
    private static ReleaseInfo Rel(string version, bool pre = false, bool draft = false) =>
        new("v" + version, version, "v" + version, "", "", null, pre, draft, null);

    [Fact]
    public void Evaluate()
    {
        Assert.Equal(UpdateCheckStatus.Available, UpdateChecker.Evaluate(Rel("1.0.3"), "1.0.2", null, manual: false));
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(Rel("1.0.2"), "1.0.2", null, manual: false));
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(Rel("1.0.1"), "1.0.2", null, manual: true));
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(Rel("1.0.3", pre: true), "1.0.2", null, manual: true));
        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Evaluate(Rel("1.0.3", draft: true), "1.0.2", null, manual: true));
        // a skipped version is not offered automatically, but a manual check shows it again
        Assert.Equal(UpdateCheckStatus.Skipped, UpdateChecker.Evaluate(Rel("1.0.3"), "1.0.2", "v1.0.3", manual: false));
        Assert.Equal(UpdateCheckStatus.Available, UpdateChecker.Evaluate(Rel("1.0.3"), "1.0.2", "1.0.3", manual: true));
        // a newer version than the skipped one is offered again
        Assert.Equal(UpdateCheckStatus.Available, UpdateChecker.Evaluate(Rel("1.0.4"), "1.0.2", "1.0.3", manual: false));
    }

    [Fact]
    public void IsDue()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateChecker.IsDue(new UpdateSettings { AutoCheck = false }, now));
        Assert.True(UpdateChecker.IsDue(new UpdateSettings(), now));
        Assert.False(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddHours(-23) }, now));
        Assert.True(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddHours(-25) }, now));
        // a recent failure postpones the next try
        Assert.False(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddHours(-25) }, now, now.AddHours(-1)));
        Assert.True(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddHours(-25) }, now, now.AddHours(-4)));
        // the clock went back by more than an hour: check instead of waiting days
        Assert.True(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddHours(2) }, now));
        Assert.False(UpdateChecker.IsDue(new UpdateSettings { LastCheckUtc = now.AddMinutes(30) }, now));
    }

    [Fact]
    public async Task Check_Available()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(ReleaseInfoTests.ReleaseJson(tag: "v1.0.9")));
        using var c = new UpdateChecker("1.0.2", "AlanBacker/VR-AirBridge-Hotspot", http);
        var r = await c.CheckAsync(null, manual: false);
        Assert.Equal(UpdateCheckStatus.Available, r.Status);
        Assert.True(r.IsAvailable);
        Assert.Equal("1.0.9", r.Release!.Version);
        var req = Assert.Single(http.Requests);
        Assert.Equal("https://api.github.com/repos/AlanBacker/VR-AirBridge-Hotspot/releases/latest", req.RequestUri!.ToString());
        Assert.Contains("VRAirBridgeHotspot/", req.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Check_UpToDate_And_NoRelease()
    {
        using (var c = new UpdateChecker("1.0.9", handler: new FakeHttp(_ => FakeHttp.Json(ReleaseInfoTests.ReleaseJson(tag: "v1.0.9")))))
            Assert.Equal(UpdateCheckStatus.UpToDate, (await c.CheckAsync(null, manual: true)).Status);
        // 404: the repository has no release yet
        using (var c = new UpdateChecker("1.0.2", handler: new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
            Assert.Equal(UpdateCheckStatus.UpToDate, (await c.CheckAsync(null, manual: true)).Status);
    }

    [Fact]
    public async Task Check_Failures_DoNotThrow()
    {
        var limited = new FakeHttp(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.Add("x-ratelimit-remaining", "0");
            return resp;
        });
        using (var c = new UpdateChecker("1.0.2", handler: limited))
        {
            var r = await c.CheckAsync(null, manual: true);
            Assert.Equal(UpdateCheckStatus.Failed, r.Status);
            Assert.Equal(Loc.T("update.rateLimited"), r.Error);
        }
        using (var c = new UpdateChecker("1.0.2", handler: new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))))
        {
            var r = await c.CheckAsync(null, manual: true);
            Assert.Equal(UpdateCheckStatus.Failed, r.Status);
            Assert.StartsWith("HTTP 502", r.Error);
        }
        using (var c = new UpdateChecker("1.0.2", handler: new FakeHttp(_ => FakeHttp.Json("<html>captive portal</html>"))))
            Assert.Equal(UpdateCheckStatus.Failed, (await c.CheckAsync(null, manual: true)).Status);
        using (var c = new UpdateChecker("1.0.2", handler: new FakeHttp((_, _) => throw new HttpRequestException("offline"))))
        {
            var r = await c.CheckAsync(null, manual: true);
            Assert.Equal(UpdateCheckStatus.Failed, r.Status);
            Assert.Equal("offline", r.Error);
        }
    }

    [Fact]
    public async Task Check_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var c = new UpdateChecker("1.0.2", handler: new FakeHttp(_ => FakeHttp.Json("{}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.CheckAsync(null, manual: true, cts.Token));
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vrab-update-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, AppInfo.ExeName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Program(string marker, int size = 200_000)
    {
        var b = new byte[size];
        new Random(marker.Length * 7919 + marker[0]).NextBytes(b);
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        var m = Encoding.ASCII.GetBytes(marker);
        Array.Copy(m, 0, b, 2, m.Length);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ReleaseInfo Release(byte[] content, string? sha = null, long? size = null) =>
        new("v1.0.9", "1.0.9", "v1.0.9", "", "", null, false, false,
            new ReleaseAsset(AppInfo.ExeName, size ?? content.Length, "https://example.com/" + AppInfo.ExeName, sha ?? Sha(content)));

    private sealed class SyncProgress : IProgress<UpdateProgress>
    {
        public readonly List<UpdateProgress> Reports = new();
        public void Report(UpdateProgress value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task Download_Verified()
    {
        var data = Program("new");
        var http = new FakeHttp(_ => FakeHttp.Bytes(data));
        using var inst = new UpdateInstaller(_exe, http);
        var progress = new SyncProgress();
        var r = await inst.DownloadAsync(Release(data), progress, CancellationToken.None);
        Assert.True(r.Ok, r.Problem + " " + r.Detail);
        Assert.Equal(data, File.ReadAllBytes(inst.DownloadPath));
        Assert.Equal(data.Length, progress.Reports[^1].Received);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);
        Assert.Equal("https://example.com/" + AppInfo.ExeName, Assert.Single(http.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Download_HashMismatch_IsDiscarded()
    {
        var data = Program("new");
        using var inst = new UpdateInstaller(_exe, new FakeHttp(_ => FakeHttp.Bytes(data)));
        var r = await inst.DownloadAsync(Release(data, sha: new string('0', 64)), null, CancellationToken.None);
        Assert.Equal(UpdateProblem.HashMismatch, r.Problem);
        Assert.Equal(Sha(data), r.Detail);
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public async Task Download_SizeMismatch_IsDiscarded()
    {
        var data = Program("new");
        using var inst = new UpdateInstaller(_exe, new FakeHttp(_ => FakeHttp.Bytes(data)));
        // shorter than announced
        var shortR = await inst.DownloadAsync(Release(data, size: data.Length + 10), null, CancellationToken.None);
        Assert.Equal(UpdateProblem.SizeMismatch, shortR.Problem);
        Assert.False(File.Exists(inst.DownloadPath));
        // longer than announced: stops reading and fails
        var longR = await inst.DownloadAsync(Release(data, size: 1000), null, CancellationToken.None);
        Assert.Equal(UpdateProblem.SizeMismatch, longR.Problem);
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public async Task Download_NotAProgram()
    {
        var data = Encoding.UTF8.GetBytes("<html>blocked by proxy</html>");
        using var inst = new UpdateInstaller(_exe, new FakeHttp(_ => FakeHttp.Bytes(data)));
        var r = await inst.DownloadAsync(Release(data), null, CancellationToken.None);
        Assert.Equal(UpdateProblem.NotExecutable, r.Problem);
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public async Task Download_HttpError()
    {
        var data = Program("new");
        using var inst = new UpdateInstaller(_exe, new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var r = await inst.DownloadAsync(Release(data), null, CancellationToken.None);
        Assert.Equal(UpdateProblem.Download, r.Problem);
        Assert.StartsWith("HTTP 404", r.Detail);
    }

    [Fact]
    public async Task Download_Cancelled()
    {
        var data = Program("new");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var inst = new UpdateInstaller(_exe, new FakeHttp(_ => FakeHttp.Bytes(data)));
        var r = await inst.DownloadAsync(Release(data), null, cts.Token);
        Assert.Equal(UpdateProblem.Cancelled, r.Problem);
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public async Task Download_CancelledWhileReceiving()
    {
        var data = Program("new");
        using var cts = new CancellationTokenSource();
        // the body never ends until the token is cancelled
        var http = new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream(cts)) }));
        using var inst = new UpdateInstaller(_exe, http);
        var r = await inst.DownloadAsync(Release(data), null, cts.Token);
        Assert.Equal(UpdateProblem.Cancelled, r.Problem);
        Assert.False(File.Exists(inst.DownloadPath));
    }

    /// <summary>Returns some data, then cancels the token and blocks until the read is cancelled.</summary>
    private sealed class EndlessStream : Stream
    {
        private readonly CancellationTokenSource _cts;
        private int _reads;
        public EndlessStream(CancellationTokenSource cts) => _cts = cts;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (++_reads == 3) _cts.Cancel();
            if (_reads >= 3) await Task.Delay(Timeout.Infinite, ct);
            int n = Math.Min(buffer.Length, 1000);
            buffer.Span[..n].Fill((byte)'M');
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Download_NoChecksum_Refused()
    {
        var data = Program("new");
        var http = new FakeHttp(_ => FakeHttp.Bytes(data));
        using var inst = new UpdateInstaller(_exe, http);
        var rel = new ReleaseInfo("v1.0.9", "1.0.9", "v1.0.9", "no hash", "", null, false, false,
            new ReleaseAsset(AppInfo.ExeName, data.Length, "https://example.com/x.exe", null));
        var r = await inst.DownloadAsync(rel, null, CancellationToken.None);
        Assert.Equal(UpdateProblem.NoChecksum, r.Problem);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Apply_SwapsAndStarts()
    {
        var oldExe = Program("old");
        var newExe = Program("new");
        File.WriteAllBytes(_exe, oldExe);
        File.WriteAllBytes(_exe + ".download", newExe);
        string? launchedPath = null, launchedArgs = null;
        using var inst = new UpdateInstaller(_exe) { Launcher = (p, a) => { launchedPath = p; launchedArgs = a; return new LaunchResult(true); } };
        var r = inst.Apply(Release(newExe), "1.0.2");
        Assert.True(r.Ok, r.Problem + " " + r.Detail);
        Assert.Equal(newExe, File.ReadAllBytes(_exe));
        Assert.Equal(oldExe, File.ReadAllBytes(inst.OldPath));
        Assert.False(File.Exists(inst.DownloadPath));
        Assert.Equal(inst.ExePath, launchedPath);
        Assert.Equal("--relaunch-from " + Environment.ProcessId + " --updated-from 1.0.2", launchedArgs);
    }

    [Theory]
    [InlineData(false, UpdateProblem.StartFailed)]
    [InlineData(true, UpdateProblem.Quarantined)]
    public void Apply_StartFails_RollsBack(bool blocked, UpdateProblem expected)
    {
        var oldExe = Program("old");
        var newExe = Program("new");
        File.WriteAllBytes(_exe, oldExe);
        File.WriteAllBytes(_exe + ".download", newExe);
        using var inst = new UpdateInstaller(_exe) { Launcher = (_, _) => new LaunchResult(false, "exited with code 1", blocked) };
        var r = inst.Apply(Release(newExe), "1.0.2");
        Assert.Equal(expected, r.Problem);
        Assert.Equal("exited with code 1", r.Detail);
        Assert.Equal(oldExe, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(inst.OldPath));
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public void Apply_LauncherThrows_RollsBack()
    {
        var oldExe = Program("old");
        var newExe = Program("new");
        File.WriteAllBytes(_exe, oldExe);
        File.WriteAllBytes(_exe + ".download", newExe);
        using var inst = new UpdateInstaller(_exe) { Launcher = (_, _) => throw new InvalidOperationException("boom") };
        var r = inst.Apply(Release(newExe), "1.0.2");
        Assert.Equal(UpdateProblem.StartFailed, r.Problem);
        Assert.Equal(oldExe, File.ReadAllBytes(_exe));
    }

    [Fact]
    public void Apply_ChangedDownload_IsRefused()
    {
        var oldExe = Program("old");
        var newExe = Program("new");
        File.WriteAllBytes(_exe, oldExe);
        File.WriteAllBytes(_exe + ".download", Program("tampered"));
        bool launched = false;
        using var inst = new UpdateInstaller(_exe) { Launcher = (_, _) => { launched = true; return new LaunchResult(true); } };
        var r = inst.Apply(Release(newExe), "1.0.2");
        Assert.Equal(UpdateProblem.HashMismatch, r.Problem);
        Assert.False(launched);
        Assert.Equal(oldExe, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(inst.DownloadPath));
    }

    [Fact]
    public void Apply_DownloadGone_MeansQuarantined()
    {
        var oldExe = Program("old");
        File.WriteAllBytes(_exe, oldExe);
        using var inst = new UpdateInstaller(_exe) { Launcher = (_, _) => new LaunchResult(true) };
        var r = inst.Apply(Release(Program("new")), "1.0.2");
        Assert.Equal(UpdateProblem.Quarantined, r.Problem);
        Assert.Equal(oldExe, File.ReadAllBytes(_exe));
    }

    [Fact]
    public void Apply_ReplacesStaleOldFile()
    {
        var oldExe = Program("old");
        var newExe = Program("new");
        File.WriteAllBytes(_exe, oldExe);
        File.WriteAllBytes(_exe + ".old", Program("older"));
        File.WriteAllBytes(_exe + ".download", newExe);
        using var inst = new UpdateInstaller(_exe) { Launcher = (_, _) => new LaunchResult(true) };
        Assert.True(inst.Apply(Release(newExe), "1.0.2").Ok);
        Assert.Equal(oldExe, File.ReadAllBytes(inst.OldPath));
    }

    [Fact]
    public async Task Cleanup_DeletesLeftovers()
    {
        File.WriteAllBytes(_exe, Program("new"));
        File.WriteAllText(_exe + ".old", "x");
        File.WriteAllText(_exe + ".download", "x");
        await UpdateInstaller.CleanupLeftoversAsync(_exe, TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(_exe));
        Assert.False(File.Exists(_exe + ".old"));
        Assert.False(File.Exists(_exe + ".download"));
    }

    [Fact]
    public void Helpers()
    {
        var data = Program("x");
        var p = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(p, data);
        Assert.True(UpdateInstaller.MatchesHash(p, data.Length, Sha(data).ToUpperInvariant()));
        Assert.True(UpdateInstaller.MatchesHash(p, 0, Sha(data)));
        Assert.False(UpdateInstaller.MatchesHash(p, data.Length + 1, Sha(data)));
        Assert.True(UpdateInstaller.LooksLikeExe(p));
        File.WriteAllText(p, "PK zip");
        Assert.False(UpdateInstaller.LooksLikeExe(p));
        Assert.False(UpdateInstaller.LooksLikeExe(Path.Combine(_dir, "missing.exe")));
        Assert.Equal(@"Local\VRAirBridgeHotspot.UpdateReady.42", UpdateInstaller.ReadyEventName(42));
    }

    [Fact]
    public void SignalReady_SetsTheEvent()
    {
        int fakePid = 900_000 + Environment.ProcessId % 1000;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateInstaller.ReadyEventName(fakePid));
        UpdateInstaller.SignalReady(fakePid);
        Assert.True(ready.WaitOne(1000));
        UpdateInstaller.SignalReady(fakePid + 1); // nobody waiting: no exception
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VRAirBridge.Core.Update;

/// <summary>The program file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Sha256);

/// <summary>A GitHub release as the update checker needs it (parsed from the REST API "releases/latest" JSON).</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Version,
    string Name,
    string Body,
    string HtmlUrl,
    DateTime? PublishedUtc,
    bool Prerelease,
    bool Draft,
    ReleaseAsset? Exe)
{
    /// <summary>"v1.0.3".</summary>
    public string DisplayVersion => "v" + Version;

    /// <summary>
    /// The SHA-256 the download must have: the asset digest GitHub computes on upload ("sha256:..."), otherwise a 64-hex
    /// value from the release notes (the line that names the exe, or the only one there is). Lower case; null = unknown.
    /// </summary>
    public string? ExpectedSha256 => Exe?.Sha256 ?? ShaFromBody(Body, Exe?.Name ?? AppInfo.ExeName);

    private static readonly Regex Hex64 = new("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);

    public static string? ShaFromBody(string? body, string exeName)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.Contains(exeName, StringComparison.OrdinalIgnoreCase)) continue;
            var m = Hex64.Matches(line);
            if (m.Count == 1) return m[0].Value.ToLowerInvariant();
        }
        var all = Hex64.Matches(body).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>Parses one release object. Throws <see cref="FormatException"/> when it is not one.</summary>
    public static ReleaseInfo Parse(string json, string exeName = AppInfo.ExeName)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException(ex.Message, ex); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("not a release object");
            var tag = Str(r, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) throw new FormatException("tag_name missing");
            var version = AppInfo.StripMetadata(tag);
            if (!UpdateVersion.TryParse(version, out _)) throw new FormatException("tag_name is not a version: " + tag);

            ReleaseAsset? exe = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var list = assets.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object)
                    .Select(ParseAsset)
                    .OfType<ReleaseAsset>()
                    .ToList();
                // the setup is never the program file, even when it is the only exe
                var exes = list.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                           && !a.Name.Equals(AppInfo.SetupName, StringComparison.OrdinalIgnoreCase)).ToList();
                exe = list.FirstOrDefault(a => a.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                      ?? (exes.Count == 1 ? exes[0] : null);
            }

            DateTime? published = null;
            if (DateTime.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)) published = p;

            var name = Str(r, "name");
            return new ReleaseInfo(tag.Trim(), version, string.IsNullOrWhiteSpace(name) ? tag.Trim() : name.Trim(), Str(r, "body") ?? "",
                Str(r, "html_url") ?? "", published, Bool(r, "prerelease"), Bool(r, "draft"), exe);
        }
    }

    private static ReleaseAsset? ParseAsset(JsonElement a)
    {
        var name = Str(a, "name");
        var url = Str(a, "browser_download_url");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var v) ? v : 0;
        return new ReleaseAsset(name, size, url, ParseDigest(Str(a, "digest")));
    }

    /// <summary>"sha256:ABC..." (64 hex) to "abc..."; anything else gives null.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var d = digest.Trim();
        const string prefix = "sha256:";
        if (!d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = d[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

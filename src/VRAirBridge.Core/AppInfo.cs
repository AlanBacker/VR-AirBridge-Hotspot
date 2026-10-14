using System.Reflection;

namespace VRAirBridge.Core;

/// <summary>
/// Single source of the app version: &lt;Version&gt; in src/Directory.Build.props, read back at run time from
/// AssemblyInformationalVersion with any "+metadata" (source revision) stripped. Every place that shows or compares
/// the version (startup log line, --diag, About text, update checker) must use <see cref="Version"/>.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "VR Air Bridge Hotspot";

    /// <summary>Repository that publishes the releases the update checker looks at.</summary>
    public const string GitHubRepo = "AlanBacker/VR-AirBridge-Hotspot";

    /// <summary>File name of the single-file release asset.</summary>
    public const string ExeName = "VRAirBridgeHotspot.exe";

    /// <summary>"1.0.2" (no "v", no "+commit").</summary>
    public static string Version { get; } = ReadVersion(typeof(AppInfo).Assembly);

    /// <summary>"v1.0.2".</summary>
    public static string DisplayVersion => "v" + Version;

    /// <summary>User-Agent for HTTP requests: "VRAirBridgeHotspot/1.0.2".</summary>
    public static string UserAgent => "VRAirBridgeHotspot/" + Version;

    public static string ReadVersion(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var v = StripMetadata(info);
        if (v.Length > 0) return v;
        var n = asm.GetName().Version;
        return n == null ? "0.0.0" : $"{n.Major}.{Math.Max(0, n.Minor)}.{Math.Max(0, n.Build)}";
    }

    /// <summary>"1.0.2+abc123" -&gt; "1.0.2"; "v1.0.2" -&gt; "1.0.2"; null -&gt; "".</summary>
    public static string StripMetadata(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var v = version.Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        return v.Trim();
    }
}

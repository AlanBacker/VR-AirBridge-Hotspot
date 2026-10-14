using System.Text;
using System.Text.Json;

namespace VRAirBridge.Core.Settings;

/// <summary>
/// Versions up to 1.0.2 kept their files next to the exe (or in %ProgramData%\VRAirBridgeHotspot when that folder was
/// not writable). On the first start of a newer version, wherever that one runs from, they move to the data folder, once: config.json (only when it
/// is ours), dhcp-leases.json, config.json.bad-*, diag-*.txt and logs\app-*.log. The extracted driver copy is deleted
/// instead (it is extracted again when needed). Other files in those folders are never touched, and nothing happens
/// once the data folder has a config.json. Run it while holding the single-instance lock, before the logger starts.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>From = the old folder; Moved = files moved or deleted; Failed = names that could not be moved.</summary>
    public sealed record Result(string From, int Moved, IReadOnlyList<string> Failed);

    /// <summary>
    /// Where older versions kept their files: next to this exe, next to the exes in <paramref name="recordedExePaths"/>
    /// (the autostart task and the firewall rules still name the portable exe that an installed copy replaces), then the
    /// %ProgramData% fallback. Lazy: the recorded paths are looked up only when the data folder has no config.json.
    /// </summary>
    public static IEnumerable<string> Candidates(string exeDir, Func<IEnumerable<string>>? recordedExePaths = null)
    {
        yield return exeDir;
        if (recordedExePaths != null)
            foreach (var exe in recordedExePaths())
                if (FolderOf(exe) is { } dir) yield return dir;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SettingsStore.FolderName);
    }

    private static string? FolderOf(string? exePath)
    {
        try
        {
            var p = exePath?.Trim().Trim('"');
            return string.IsNullOrEmpty(p) || !Path.IsPathFullyQualified(p) ? null : Path.GetDirectoryName(p);
        }
        catch { return null; }
    }

    /// <summary>The config.json an older version left in one of <paramref name="oldDirs"/>, or null.</summary>
    public static string? FindOldConfig(string dataDir, IEnumerable<string> oldDirs) =>
        Source(dataDir, oldDirs) is { } d ? Path.Combine(d, SettingsStore.ConfigFileName) : null;

    /// <summary>Moves the files; null when there was nothing to move.</summary>
    public static Result? Run(string dataDir, IEnumerable<string> oldDirs, IEnumerable<string> driverFiles)
    {
        var from = Source(dataDir, oldDirs);
        if (from == null) return null;
        Directory.CreateDirectory(dataDir);
        int moved = 0;
        var failed = new List<string>();

        void Move(string src, string dest)
        {
            if (File.Exists(dest)) return; // keep what the data folder already has
            try
            {
                File.Move(src, dest);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetRelativePath(from, src));
            }
        }

        foreach (var pattern in new[] { SettingsStore.LeaseFileName, SettingsStore.ConfigFileName + ".bad-*", "diag-*.txt" })
            foreach (var f in Files(from, pattern))
                Move(f, Path.Combine(dataDir, Path.GetFileName(f)));

        var oldLogs = Path.Combine(from, SettingsStore.LogFolderName);
        var logs = Files(oldLogs, "app-*.log");
        if (logs.Count > 0)
        {
            var newLogs = Path.Combine(dataDir, SettingsStore.LogFolderName);
            Directory.CreateDirectory(newLogs);
            foreach (var f in logs) Move(f, Path.Combine(newLogs, Path.GetFileName(f)));
        }
        DeleteIfEmpty(oldLogs);

        var driver = Path.Combine(from, "driver");
        foreach (var name in driverFiles)
        {
            var f = Path.Combine(driver, Path.GetFileName(name));
            try
            {
                if (!File.Exists(f)) continue;
                File.Delete(f);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DeleteIfEmpty(driver);

        // last: once config.json is in the data folder, this never runs again
        Move(Path.Combine(from, SettingsStore.ConfigFileName), Path.Combine(dataDir, SettingsStore.ConfigFileName));
        return new Result(from, moved, failed);
    }

    private static string? Source(string dataDir, IEnumerable<string> oldDirs)
    {
        if (File.Exists(Path.Combine(dataDir, SettingsStore.ConfigFileName))) return null;
        var data = Normalize(dataDir);
        foreach (var dir in oldDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var d = Normalize(dir);
            if (string.Equals(d, data, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOurConfig(Path.Combine(d, SettingsStore.ConfigFileName))) return d;
        }
        return null;
    }

    /// <summary>A JSON object with the "ssid", "dhcp" and "revert" settings every version wrote.</summary>
    public static bool IsOurConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(doc.RootElement.EnumerateObject().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            return names.Contains("ssid") && names.Contains("dhcp") && names.Contains("revert");
        }
        catch { return false; }
    }

    private static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static List<string> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList() : new(); }
        catch { return new(); }
    }

    private static void DeleteIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch { }
    }
}

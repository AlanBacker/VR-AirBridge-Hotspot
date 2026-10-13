using System.Globalization;
using System.Text.RegularExpressions;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

/// <summary>“Meta 软件残留”诊断的用户可见文字（从字符串表读取，随界面语言切换）。</summary>
public static class MetaStrings
{
    public static string Section => Loc.T("meta.section");
    public static string ReadOnlyNote => Loc.T("meta.readOnlyNote");
    public static string NoneFound => Loc.T("meta.noneFound");
    public static string FolderMissing => Loc.T("meta.folderMissing");
    public static string DriverStoreLine => Loc.T("meta.driverStoreLine");
    public static string DriverStoreHint => Loc.T("meta.driverStoreHint");
    public static string OrphanRepo => Loc.T("meta.orphanRepo");
    public static string OvrToolkitTask => Loc.T("meta.ovrToolkitTask");
    public static string UninstallRemovesDriver => Loc.T("meta.uninstallRemovesDriver");
}

public sealed record LeftoverFolder(string Path, bool Exists, long Bytes, int Files);

/// <summary>驱动库（pnputil /enum-drivers）里的一个驱动包。</summary>
public sealed record DriverStoreEntry(string PublishedName, string OriginalName, string? Provider, string? Version);

public sealed record OrphanRepositoryFolder(string Name, long Bytes);

public sealed record MetaLeftoverReport(
    IReadOnlyList<LeftoverFolder> Folders,
    IReadOnlyList<DriverStoreEntry> Drivers,
    IReadOnlyList<OrphanRepositoryFolder> OrphanRepositoryFolders,
    IReadOnlyList<string> OvrToolkitTasks)
{
    public bool Any => Folders.Any(f => f.Exists) || Drivers.Count > 0 || OrphanRepositoryFolders.Count > 0;

    public IEnumerable<string> DescribeLines()
    {
        yield return MetaStrings.ReadOnlyNote;
        foreach (var f in Folders)
            yield return f.Exists ? Loc.T("meta.files", f.Path, MetaLeftovers.FormatSize(f.Bytes), f.Files) : $"{f.Path}: {MetaStrings.FolderMissing}";
        foreach (var d in Drivers)
        {
            yield return string.Format(CultureInfo.InvariantCulture, MetaStrings.DriverStoreLine, d.PublishedName, d.OriginalName, d.Provider ?? "?", d.Version ?? "?");
            yield return "  " + string.Format(CultureInfo.InvariantCulture, MetaStrings.DriverStoreHint, d.PublishedName);
        }
        foreach (var o in OrphanRepositoryFolders)
            yield return string.Format(CultureInfo.InvariantCulture, MetaStrings.OrphanRepo, o.Name, MetaLeftovers.FormatSize(o.Bytes));
        foreach (var t in OvrToolkitTasks)
            yield return string.Format(CultureInfo.InvariantCulture, MetaStrings.OvrToolkitTask, t);
        if (!Any) yield return MetaStrings.NoneFound;
    }
}

/// <summary>
/// 只读检查 Meta（Oculus）软件卸载后的残留：用户目录下的 Oculus 文件夹、驱动库里的 Oculus/Facebook 驱动包。
/// 本软件不删除任何东西。
/// </summary>
public static class MetaLeftovers
{
    public static IReadOnlyList<string> CandidateFolders() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oculus"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oculus"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Oculus"),
    };

    public static MetaLeftoverReport Scan()
    {
        var folders = CandidateFolders().Select(ScanFolder).ToList();
        List<DriverStoreEntry> drivers = new();
        try
        {
            var r = ProcessRunner.Run(DriverInstaller.PnpUtilPath, "/enum-drivers", 60_000, ProcessRunner.OemEncoding());
            drivers = ParseEnumDrivers(r.StdOut).Where(IsMetaDriver).ToList();
        }
        catch (Exception ex) { Logger.Debug(Loc.T("meta.pnputilEnumDriversFailed") + ex.Message); }
        var orphans = ScanRepository(drivers);
        return new MetaLeftoverReport(folders, drivers, orphans, FindOvrToolkitTasks());
    }

    public static LeftoverFolder ScanFolder(string path)
    {
        if (!Directory.Exists(path)) return new LeftoverFolder(path, false, 0, 0);
        long bytes = 0; int files = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                try { bytes += f.Length; files++; } catch { }
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("meta.failedMeasure", path) + ex.Message); }
        return new LeftoverFolder(path, true, bytes, files);
    }

    /// <summary>驱动库里没被 pnputil 登记、但还留着的 oculus* 文件夹。</summary>
    private static List<OrphanRepositoryFolder> ScanRepository(IReadOnlyList<DriverStoreEntry> registered)
    {
        var res = new List<OrphanRepositoryFolder>();
        var repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(repo, "oculus*"))
            {
                var name = Path.GetFileName(dir);
                if (registered.Any(d => name.StartsWith(d.OriginalName + "_", StringComparison.OrdinalIgnoreCase))) continue;
                res.Add(new OrphanRepositoryFolder(name, ScanFolder(dir).Bytes));
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("meta.failedReadDriverStore") + ex.Message); }
        return res;
    }

    private static List<string> FindOvrToolkitTasks()
    {
        var res = new List<string>();
        try
        {
            var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/query /fo csv /nh", 30_000, ProcessRunner.OemEncoding());
            foreach (Match m in Regex.Matches(r.StdOut, "\"\\\\?(OVRToolkit[^\"]*)\""))
                if (!res.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) res.Add(m.Groups[1].Value);
        }
        catch (Exception ex) { Logger.Debug(Loc.T("meta.failedQueryScheduledTasks") + ex.Message); }
        return res;
    }

    public static bool IsMetaDriver(DriverStoreEntry d) =>
        Regex.IsMatch(d.OriginalName, "^oculus", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(d.Provider ?? "", @"\b(Oculus|Facebook|Meta Platforms)\b", RegexOptions.IgnoreCase);

    private static readonly Regex VersionValue = new(@"^\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\s+\d+(\.\d+){1,3}$");

    /// <summary>
    /// 解析 pnputil /enum-drivers 的输出（与系统语言无关：不认字段名，只认值的形状）。
    /// 每个驱动包：oemNN.inf → 下一个 .inf 是原始名 → 再下一个值是提供商；“日期 版本” 形状的值是版本。
    /// </summary>
    public static List<DriverStoreEntry> ParseEnumDrivers(string text)
    {
        var res = new List<DriverStoreEntry>();
        string? pub = null, orig = null, provider = null, version = null;
        bool expectOrig = false, expectProvider = false;
        void Flush()
        {
            if (pub != null && orig != null) res.Add(new DriverStoreEntry(pub, orig, provider, version));
            pub = orig = provider = version = null;
            expectOrig = expectProvider = false;
        }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var idx = raw.IndexOfAny(new[] { ':', '\uFF1A' }); // 全角冒号（中文系统的 pnputil 输出）
            if (idx < 0) continue;
            var value = raw[(idx + 1)..].Trim();
            if (value.Length == 0) continue;
            if (Regex.IsMatch(value, @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
            {
                Flush();
                pub = value.ToLowerInvariant();
                expectOrig = true;
                continue;
            }
            if (pub == null) continue;
            if (expectOrig && value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            {
                orig = value; expectOrig = false; expectProvider = true;
                continue;
            }
            if (expectProvider) { provider = value; expectProvider = false; continue; }
            if (version == null && VersionValue.IsMatch(value))
            {
                var parts = Regex.Split(value, @"\s+");
                version = $"{DriverInstaller.NormalizeDate(parts[0])} {parts[1]}";
            }
        }
        Flush();
        return res;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
        : bytes + " B";
}

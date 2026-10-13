using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

public enum DriverStateKind { DongleNotPresent, PresentNoDriver, DriverOk, DriverOtherVersion }

/// <summary>驱动库（DriverStore\FileRepository）里已放好的 netrtwlanufb 驱动包。</summary>
public sealed record StagedDriverPackage(string Folder, string? Version, string? Date);

/// <summary>D-Link DWA-F18 驱动状态。</summary>
public sealed record DriverState(DriverStateKind Kind)
{
    public string? Version { get; init; }
    public string? Date { get; init; }
    public string? Provider { get; init; }
    /// <summary>系统里实际用的 INF（例如 C:\Windows\INF\oem42.inf）；只在详细查询时有。</summary>
    public string? InfPath { get; init; }
    /// <summary>网卡的连接名（例如 “WLAN 2”）。</summary>
    public string? AdapterName { get; init; }
    /// <summary>设备管理器里的设备名（没驱动时通常是 “802.11ax WLAN Adapter”）。</summary>
    public string? DeviceName { get; init; }
    public string? PnpId { get; init; }
    /// <summary>设备管理器错误码（Win32_PnPEntity.ConfigManagerErrorCode），0 = 无错误。</summary>
    public int ErrorCode { get; init; }
    public IReadOnlyList<StagedDriverPackage> Staged { get; init; } = Array.Empty<StagedDriverPackage>();

    public bool IsInstalled => Kind is DriverStateKind.DriverOk or DriverStateKind.DriverOtherVersion;
    public bool DonglePresent => Kind != DriverStateKind.DongleNotPresent;

    public string VersionText => string.IsNullOrEmpty(Version) ? "?" : string.IsNullOrEmpty(Date) ? Version! : Loc.T("common.nameParen", Version, Date);

    /// <summary>界面上“驱动”一行显示的文字。</summary>
    public string ShortText => Kind switch
    {
        DriverStateKind.DriverOk => $"{VersionText} ✔",
        DriverStateKind.DriverOtherVersion => Loc.T("driverInstall.notBundled", VersionText, DriverStrings.BundledVersion),
        DriverStateKind.PresentNoDriver => DriverStrings.KindPresentNoDriver,
        _ => Staged.Count > 0 ? DriverStrings.KindNotPresentStaged : DriverStrings.KindNotPresent,
    };

    public string KindText => Kind switch
    {
        DriverStateKind.DriverOk => DriverStrings.KindOk,
        DriverStateKind.DriverOtherVersion => DriverStrings.KindOther,
        DriverStateKind.PresentNoDriver => DriverStrings.KindPresentNoDriver,
        _ => Staged.Count > 0 ? DriverStrings.KindNotPresentStaged : DriverStrings.KindNotPresent,
    };

    /// <summary>命令行 / 诊断信息用的多行说明。</summary>
    public IEnumerable<string> DescribeLines()
    {
        yield return Loc.T("driverInstall.status", Kind, KindText);
        if (IsInstalled)
        {
            yield return Loc.T("driverInstall.driverVersionProvider", VersionText, Provider ?? "?");
            if (AdapterName != null) yield return Loc.T("driverInstall.adapter", AdapterName);
            if (InfPath != null) yield return $"INF: {InfPath}";
        }
        if (DeviceName != null || PnpId != null) yield return Loc.T("driverInstall.device", DeviceName ?? "?", PnpId);
        if (ErrorCode != 0) yield return Loc.T("driverInstall.deviceManagerErrorCode", ErrorCode);
        if (Kind == DriverStateKind.PresentNoDriver) yield return Loc.T("driverInstall.hint") + DriverStrings.NoDriverHint;
        yield return Loc.T("driverInstall.bundledDriver", DriverStrings.BundledVersion, DriverStrings.BundledDate);
        if (Staged.Count == 0) yield return Loc.T("driverInstall.netrtwlanufbPackagesDriverStoreNone");
        foreach (var p in Staged) yield return Loc.T("driverInstall.netrtwlanufbPackageDriverStore", p.Folder, p.Version ?? "?", p.Date ?? "?");
    }
}

// ---- 用来判断状态的原始数据行（来自 WMI；单元测试里直接构造）
public sealed record PnpDeviceRow(string PnpId, string? Name, string? ClassGuid, string? Service, int ErrorCode, bool? Present);
public sealed record SignedDriverRow(string DeviceId, string? Version, string? InfName, string? Date, string? Provider);
public sealed record NetAdapterRow(string Name, string? Description, string? PnpId, string? Version, string? Date, string? Provider);

public enum PnpUtilOutcome { Installed, StagedNoDevice, RebootRequired, Failed }

public sealed record PnpUtilResult(PnpUtilOutcome Outcome, int ExitCode, string Message, string Output, string? PublishedName)
{
    public bool Ok => Outcome != PnpUtilOutcome.Failed;
}

public sealed record DriverInstallResult(bool Ok, string Message, IReadOnlyList<string> Steps, PnpUtilResult? PnpUtil, DriverState? Before, DriverState? After);

/// <summary>
/// 内置驱动的检测与安装：判断 D-Link 有没有驱动（不需要管理员），
/// 解压内置驱动并用 pnputil 装进系统（需要管理员）。
/// </summary>
public static class DriverInstaller
{
    public const int ExitInstalled = 0;
    public const int ExitNoMoreItems = 259;     // ERROR_NO_MORE_ITEMS：驱动包已加入，但没有匹配的设备
    public const int ExitRebootRequired = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED

    // ================================================================ 状态判断（纯函数，可单元测试)

    public static bool IsDongleId(string? pnpId) =>
        pnpId != null && pnpId.StartsWith(DongleInfo.HardwareIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 根据 WMI 数据判断驱动状态：
    /// 有对应的 MSFT_NetAdapter → 已装（版本等于内置版本 = DriverOk，否则 DriverOtherVersion）；
    /// 没有网卡但有 USB\VID_2001&amp;PID_3323 的 PnP 设备 → PresentNoDriver；都没有 → DongleNotPresent。
    /// </summary>
    public static DriverState Classify(
        IEnumerable<PnpDeviceRow> pnp,
        IEnumerable<SignedDriverRow> signed,
        IEnumerable<NetAdapterRow> adapters,
        IReadOnlyList<StagedDriverPackage>? staged = null,
        string bundledVersion = DriverStrings.BundledVersion)
    {
        var pnpList = pnp.ToList();
        var signedList = signed.ToList();
        var adapterList = adapters.ToList();
        staged ??= Array.Empty<StagedDriverPackage>();

        var adapter = adapterList.FirstOrDefault(a => IsDongleId(a.PnpId))
                      ?? adapterList.FirstOrDefault(a => (a.Description ?? "").Contains("DWA-F18", StringComparison.OrdinalIgnoreCase));
        if (adapter != null)
        {
            var sd = signedList.FirstOrDefault(s => string.Equals(s.DeviceId, adapter.PnpId, StringComparison.OrdinalIgnoreCase));
            var dev = pnpList.FirstOrDefault(p => string.Equals(p.PnpId, adapter.PnpId, StringComparison.OrdinalIgnoreCase));
            var version = FirstNonEmpty(adapter.Version, sd?.Version);
            return new DriverState(SameVersion(version, bundledVersion) ? DriverStateKind.DriverOk : DriverStateKind.DriverOtherVersion)
            {
                Version = version,
                Date = NormalizeDate(FirstNonEmpty(adapter.Date, sd?.Date)),
                Provider = FirstNonEmpty(adapter.Provider, sd?.Provider),
                InfPath = InfFullPath(sd?.InfName),
                AdapterName = adapter.Name,
                DeviceName = FirstNonEmpty(dev?.Name, adapter.Description),
                PnpId = adapter.PnpId,
                ErrorCode = dev?.ErrorCode ?? 0,
                Staged = staged,
            };
        }

        var device = pnpList.FirstOrDefault(p => IsDongleId(p.PnpId) && p.Present != false);
        if (device == null) return new DriverState(DriverStateKind.DongleNotPresent) { Staged = staged };
        return new DriverState(DriverStateKind.PresentNoDriver)
        {
            DeviceName = device.Name,
            PnpId = device.PnpId,
            ErrorCode = device.ErrorCode,
            Staged = staged,
        };
    }

    /// <summary>
    /// 看门狗每个周期用的快速判断：直接用已经查到的 D-Link 网卡（MSFT_NetAdapter）和 USB 设备探测结果，不再额外查询。
    /// </summary>
    public static DriverState FromProbe(DongleInfo? dongle, string? usbDeviceName)
    {
        if (dongle != null)
            return Classify(Array.Empty<PnpDeviceRow>(), Array.Empty<SignedDriverRow>(),
                new[] { new NetAdapterRow(dongle.Name, dongle.Description, dongle.PnpId, dongle.DriverVersion, dongle.DriverDate, dongle.DriverProvider) });
        if (usbDeviceName != null)
            return Classify(new[] { new PnpDeviceRow(DongleInfo.HardwareIdPrefix, usbDeviceName, null, null, 0, true) },
                Array.Empty<SignedDriverRow>(), Array.Empty<NetAdapterRow>());
        return new DriverState(DriverStateKind.DongleNotPresent);
    }

    internal static bool SameVersion(string? a, string? b)
    {
        a = a?.Trim(); b = b?.Trim();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb)) return va == vb;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? InfFullPath(string? infName)
    {
        if (string.IsNullOrWhiteSpace(infName)) return null;
        if (Path.IsPathRooted(infName)) return infName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", infName);
    }

    /// <summary>把 WMI 日期（20230831000000.******+000）、INF 日期（08/31/2023）等统一成 yyyy-MM-dd。</summary>
    public static string? NormalizeDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var cim = Regex.Match(s, @"^(\d{4})(\d{2})(\d{2})\d{6}");
        if (cim.Success) return $"{cim.Groups[1].Value}-{cim.Groups[2].Value}-{cim.Groups[3].Value}";
        if (DateTime.TryParseExact(s, new[] { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy/M/d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>从 INF 文本里读 DriverVer = 08/31/2023,1.27.831.2023。</summary>
    public static (string? Date, string? Version) ParseDriverVer(string infText)
    {
        var m = Regex.Match(infText, @"^\s*DriverVer\s*=\s*([0-9/\-]+)\s*,\s*([0-9.]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? (NormalizeDate(m.Groups[1].Value), m.Groups[2].Value) : (null, null);
    }

    // ================================================================ pnputil 结果（纯函数，可单元测试）

    /// <summary>把 pnputil 的退出码和输出（已按 OEM 代码页解码）映射成结果。</summary>
    public static PnpUtilResult MapPnpUtil(int exitCode, string? stdout, string? stderr, bool timedOut = false)
    {
        var output = CleanOutput((stdout ?? "") + "\n" + (stderr ?? ""));
        var published = Regex.Match(output, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase);
        string? pub = published.Success ? published.Value.ToLowerInvariant() : null;
        if (timedOut)
            return new PnpUtilResult(PnpUtilOutcome.Failed, exitCode, Loc.T("driverInstall.pnputilDidNotFinishTime", DriverStrings.FailedPrefix), output, pub);
        return exitCode switch
        {
            ExitInstalled => new PnpUtilResult(PnpUtilOutcome.Installed, exitCode, DriverStrings.Installed, output, pub),
            ExitNoMoreItems => new PnpUtilResult(PnpUtilOutcome.StagedNoDevice, exitCode, DriverStrings.Staged259, output, pub),
            ExitRebootRequired => new PnpUtilResult(PnpUtilOutcome.RebootRequired, exitCode, DriverStrings.Reboot3010, output, pub),
            5 or 740 => new PnpUtilResult(PnpUtilOutcome.Failed, exitCode,
                Loc.T("driverInstall.pnputilExitCodeAdministratorRights", DriverStrings.FailedPrefix, exitCode) + (output.Length > 0 ? Loc.T("common.period") + OneLine(output) : ""), output, pub),
            _ => new PnpUtilResult(PnpUtilOutcome.Failed, exitCode,
                Loc.T("driverInstall.pnputilExitCode", DriverStrings.FailedPrefix, exitCode) + (output.Length > 0 ? Loc.T("common.colon") + OneLine(output) : ""), output, pub),
        };
    }

    /// <summary>去掉 “Microsoft PnP 工具 / Microsoft PnP Utility” 标题行和空行。</summary>
    internal static string CleanOutput(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !Regex.IsMatch(l, @"^Microsoft PnP\s", RegexOptions.IgnoreCase));
        return string.Join("\n", lines);
    }

    private static string OneLine(string text)
    {
        var s = string.Join(Loc.T("common.listSep"), text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return s.Length > 400 ? s[..400] + "…" : s;
    }

    // ================================================================ 查询系统（不需要管理员）

    private const string PnpLike = @"USB\\VID_2001&PID_3323%";

    /// <summary>
    /// 查询当前驱动状态。detailed = true 时额外查 Win32_PnPSignedDriver（拿 INF 路径）和驱动库里的驱动包。
    /// 普通权限即可运行。
    /// </summary>
    public static DriverState GetState(bool detailed = true)
    {
        var pnp = QueryPnp();
        var adapters = QueryAdapters();
        var signed = detailed && (pnp.Count > 0 || adapters.Any(a => IsDongleId(a.PnpId))) ? QuerySigned() : new List<SignedDriverRow>();
        var staged = detailed ? FindStagedPackages() : new List<StagedDriverPackage>();
        return Classify(pnp, signed, adapters, staged);
    }

    internal static List<PnpDeviceRow> QueryPnp()
    {
        var list = new List<PnpDeviceRow>();
        foreach (var withPresent in new[] { true, false })
        {
            try
            {
                var fields = "Name, PNPDeviceID, ClassGuid, Service, ConfigManagerErrorCode" + (withPresent ? ", Present" : "");
                using var s = new ManagementObjectSearcher(@"root\cimv2", $"SELECT {fields} FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '{PnpLike}'");
                foreach (ManagementBaseObject o in s.Get())
                    using (o)
                        list.Add(new PnpDeviceRow(
                            o["PNPDeviceID"] as string ?? "", o["Name"] as string, o["ClassGuid"] as string, o["Service"] as string,
                            o["ConfigManagerErrorCode"] is { } ec ? Convert.ToInt32(ec, CultureInfo.InvariantCulture) : 0,
                            withPresent && o["Present"] is bool b ? b : null));
                return list;
            }
            catch (Exception ex)
            {
                list.Clear();
                Logger.Debug(Loc.T("driverInstall.failedQueryWin32Pnpentity", (withPresent ? Loc.T("driverInstall.includingPresent") : "")) + ex.Message);
            }
        }
        return list;
    }

    internal static List<NetAdapterRow> QueryAdapters()
    {
        var list = new List<NetAdapterRow>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"), new ObjectQuery(
                "SELECT Name, InterfaceDescription, PnPDeviceID, DriverVersionString, DriverDate, DriverProvider FROM MSFT_NetAdapter"));
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    list.Add(new NetAdapterRow(o["Name"] as string ?? "", o["InterfaceDescription"] as string, o["PnPDeviceID"] as string,
                        o["DriverVersionString"] as string, o["DriverDate"] as string, o["DriverProvider"] as string));
        }
        catch (Exception ex) { Logger.Debug(Loc.T("driverInstall.failedQueryMsftNetadapter") + ex.Message); }
        return list;
    }

    internal static List<SignedDriverRow> QuerySigned()
    {
        var list = new List<SignedDriverRow>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                $"SELECT DeviceID, DriverVersion, InfName, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceID LIKE '{PnpLike}'");
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    list.Add(new SignedDriverRow(o["DeviceID"] as string ?? "", o["DriverVersion"] as string, o["InfName"] as string,
                        o["DriverDate"] as string, o["DriverProviderName"] as string));
        }
        catch (Exception ex) { Logger.Debug(Loc.T("driverInstall.failedQueryWin32Pnpsigneddriver") + ex.Message); }
        return list;
    }

    /// <summary>驱动库里已有的 netrtwlanufb 驱动包（普通权限可读）。</summary>
    public static List<StagedDriverPackage> FindStagedPackages(string? repository = null)
    {
        var res = new List<StagedDriverPackage>();
        repository ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(repository, "netrtwlanufb.inf_*"))
            {
                string? date = null, ver = null;
                try { (date, ver) = ParseDriverVer(File.ReadAllText(Path.Combine(dir, "netrtwlanufb.inf"))); } catch { }
                res.Add(new StagedDriverPackage(Path.GetFileName(dir), ver, date));
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("driverInstall.failedReadDriverStore") + ex.Message); }
        return res;
    }

    // ================================================================ 安装（需要管理员）

    public static string PnpUtilPath =>
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");

    /// <summary>
    /// 解压内置驱动到 &lt;数据目录&gt;\driver（校验 SHA-256）→ pnputil /add-driver … /install →
    /// 最多等 30 秒让网卡出现 → 返回结果和安装后的状态。已经是同版本时也照样执行（= 重新安装）。
    /// </summary>
    public static async Task<DriverInstallResult> InstallAsync(DriverPackage pkg, string dataDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        void Step(string text, LogLevel level = LogLevel.Info)
        {
            steps.Add(text);
            Logger.Write(level, Loc.T("driverInstall.installDriver") + text);
            try { progress?.Report(text); } catch { }
        }
        DriverInstallResult Fail(string msg, PnpUtilResult? r = null, DriverState? before = null)
        {
            Step(msg, LogLevel.Error);
            return new DriverInstallResult(false, msg, steps, r, before, null);
        }

        if (!AdminUtil.IsAdministrator()) return Fail(DriverStrings.NeedAdmin);

        var before = await Task.Run(() => GetState(detailed: false), ct);
        Step(Loc.T("driverInstall.beforeInstall") + before.ShortText);
        if (before.Kind == DriverStateKind.DriverOk) Step(Loc.T("driverInstall.alreadyBundledTreatingAsReinstall", DriverStrings.BundledVersion));

        // 1. 解压 + 校验
        DriverManifest manifest;
        try { manifest = pkg.Manifest; }
        catch (Exception ex) { return Fail(DriverStrings.VerifyFailed + Loc.T("common.colon") + ex.Message, before: before); }
        var dir = Path.Combine(dataDirectory, "driver");
        Step(Loc.T("driverInstall.extractingBundledDriverVerifyingSha", manifest.DriverVersion, dir));
        int count;
        try
        {
            count = await Task.Run(() => pkg.ExtractTo(dir), ct);
        }
        catch (DriverPackageException ex) { return Fail(DriverStrings.VerifyFailed + Loc.T("common.colon") + ex.Message, before: before); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRAirBridgeHotspot", "driver");
            if (string.Equals(Path.GetFullPath(alt), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                return Fail(Loc.T("driverInstall.failedExtractDriver") + ex.Message, before: before);
            Step(Loc.T("driverInstall.cannotWriteUsingInstead", dir, ex.Message, alt), LogLevel.Warn);
            dir = alt;
            try { count = await Task.Run(() => pkg.ExtractTo(dir), ct); }
            catch (DriverPackageException ex2) { return Fail(DriverStrings.VerifyFailed + Loc.T("common.colon") + ex2.Message, before: before); }
            catch (Exception ex2) { return Fail(Loc.T("driverInstall.failedExtractDriver") + ex2.Message, before: before); }
        }
        Step(Loc.T("driverInstall.extractedFilesAllSha256", count), LogLevel.Ok);
        ct.ThrowIfCancellationRequested();

        // 2. pnputil
        var inf = Path.Combine(dir, manifest.Inf);
        var args = $"/add-driver \"{inf}\" /install";
        Step(Loc.T("driverInstall.runningPnputilMayTake10", args));
        var pr = await Task.Run(() => ProcessRunner.Run(PnpUtilPath, args, 300_000, ProcessRunner.OemEncoding()), CancellationToken.None);
        foreach (var line in CleanOutput(pr.StdOut + "\n" + pr.StdErr).Split('\n').Where(l => l.Length > 0))
            Logger.Debug("pnputil: " + line);
        var map = MapPnpUtil(pr.ExitCode, pr.StdOut, pr.StdErr, pr.TimedOut);
        Step(Loc.T("driverInstall.pnputilExitCode2", pr.ExitCode) + (map.PublishedName != null ? Loc.T("driverInstall.publishedName", map.PublishedName) : ""), map.Ok ? LogLevel.Info : LogLevel.Error);
        if (!map.Ok) return Fail(map.Message, map, before);

        // 3. 等网卡出现
        DongleInfo.InvalidateUsbProbe();
        bool usbPresent = before.DonglePresent || QueryPnp().Any(p => IsDongleId(p.PnpId) && p.Present != false);
        if (usbPresent && map.Outcome != PnpUtilOutcome.RebootRequired)
        {
            Step(Loc.T("driverInstall.waitingDLinkAdapterAppear"));
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DongleInfo.Find() == null && DateTime.UtcNow < until)
                await Task.Delay(1000, ct);
        }
        var after = await Task.Run(() => GetState(detailed: true), CancellationToken.None);
        DongleInfo.InvalidateUsbProbe();
        Step(Loc.T("driverInstall.afterInstall") + after.ShortText, after.IsInstalled ? LogLevel.Ok : LogLevel.Warn);

        string message = map.Outcome switch
        {
            PnpUtilOutcome.RebootRequired => DriverStrings.Reboot3010,
            _ when after.IsInstalled => Loc.T("common.labelValue", DriverStrings.Installed, after.ShortText) + (after.AdapterName != null ? Loc.T("driverInstall.adapter2", after.AdapterName) : ""),
            PnpUtilOutcome.StagedNoDevice when after.DonglePresent => DriverStrings.Staged259 + DriverStrings.Staged259Present,
            _ when after.DonglePresent => DriverStrings.NoAdapterYet,
            _ => DriverStrings.Staged259,
        };
        Step(message, LogLevel.Ok);
        return new DriverInstallResult(true, message, steps, map, before, after);
    }
}

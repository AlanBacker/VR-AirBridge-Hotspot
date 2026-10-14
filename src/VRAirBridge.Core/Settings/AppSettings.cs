using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.Settings;

public sealed class DhcpSettings
{
    /// <summary>auto = ICS 的 DHCP 不工作时由本软件提供；always = 总是由本软件提供；off = 不提供</summary>
    public string Mode { get; set; } = "auto";
    public string PoolStart { get; set; } = "192.168.137.100";
    public string PoolEnd { get; set; } = "192.168.137.199";
    public double LeaseHours { get; set; } = 12;
    /// <summary>可选：DHCP 选项 15（域名），留空则不下发</summary>
    public string DomainName { get; set; } = "";
    /// <summary>上行 DNS 之后追加的备用 DNS</summary>
    public List<string> FallbackDns { get; set; } = new() { "223.5.5.5", "8.8.8.8" };
}

public sealed class NatSettings
{
    /// <summary>auto = ICS 不工作时创建 WinNAT；off = 不管 NAT</summary>
    public string Mode { get; set; } = "auto";
    public string Name { get; set; } = "VRAirBridgeNat";
}

public sealed class InternetSettings
{
    /// <summary>
    /// true（默认）= 连接热点的设备可以经由电脑上网（WinNAT + IPv4 转发 + DHCP 下发 DNS）；
    /// false = 仅局域网：不做 NAT、热点网卡不转发、DHCP 不下发 DNS。电脑和头显之间的串流不受影响。
    /// </summary>
    public bool Allow { get; set; } = true;
}

public sealed class ChannelSettings
{
    /// <summary>
    /// 实验功能“钉住信道”：开热点前先让 D-Link 连到这个 Wi-Fi（5GHz），热点会跟它用同一个信道。
    /// 空 = 不钉住（默认）。驱动 / 系统不支持时会自动回退到不钉住。
    /// </summary>
    public string PinSsid { get; set; } = "";
}

public sealed class UiSettings
{
    /// <summary>auto = 跟随 Windows 显示语言；en | zh-CN | ja | ko</summary>
    public string Language { get; set; } = "auto";
    /// <summary>system = 跟随 Windows 应用主题；dark | light</summary>
    public string Theme { get; set; } = "system";
    /// <summary>侧栏“高级”开关：显示 DHCP 地址池、钉住信道、调试日志、Meta 残留等高级内容。</summary>
    public bool Advanced { get; set; }
    /// <summary>“开始使用”向导已完成（热点成功运行过一次或用户点了跳过）。</summary>
    public bool SetupDone { get; set; }
}

/// <summary>Update check against the GitHub releases of the app.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check for a new version shortly after start and then once a day.</summary>
    public bool AutoCheck { get; set; } = true;
    /// <summary>When the last check finished (successfully or not), UTC.</summary>
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>"1.0.3": the user clicked "Skip this version" for it; newer versions are offered again.</summary>
    public string SkippedVersion { get; set; } = "";
}

/// <summary>记录本软件对系统做过的改动，供“还原”使用。</summary>
public sealed class RevertState
{
    public List<string> DisabledAdapters { get; set; } = new();
    public List<string> ForwardingEnabledOn { get; set; } = new();
    public bool OvrServiceChangedToManual { get; set; }
    public bool NatCreatedByUs { get; set; }
    /// <summary>“钉住信道”复制到 D-Link 上的 Wi-Fi 配置文件，格式 “网卡GUID|配置文件名”。</summary>
    public List<string> PinProfilesAdded { get; set; } = new();
    /// <summary>“钉住信道”改过跃点数的 D-Link 接口（0 = 没改过）以及原来的设置。</summary>
    public int PinMetricIfIndex { get; set; }
    public bool PinMetricWasAutomatic { get; set; } = true;
    public int PinMetricPrevious { get; set; }
}

public sealed class AppSettings
{
    public string Ssid { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>5GHz | 2.4GHz | Auto</summary>
    public string Band { get; set; } = "5GHz";
    /// <summary>WPA2 | WPA3Transition | WPA3</summary>
    public string Auth { get; set; } = "WPA2";
    /// <summary>开机自启动（任务计划，最高权限）</summary>
    public bool AutoStart { get; set; }
    /// <summary>软件启动时自动开热点</summary>
    public bool StartHotspotOnLaunch { get; set; } = true;
    /// <summary>最小化 / 点关闭按钮时缩到托盘（程序继续运行，DHCP 和看门狗继续工作）</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>真正退出程序时保持热点开启（注意：退出后本软件提供的 DHCP 会停止）</summary>
    public bool KeepHotspotOnClose { get; set; }
    public bool ShowDebugLog { get; set; } = true;
    public int WatchdogIntervalSeconds { get; set; } = 4;
    public DhcpSettings Dhcp { get; set; } = new();
    public NatSettings Nat { get; set; } = new();
    public InternetSettings Internet { get; set; } = new();
    public ChannelSettings Channel { get; set; } = new();
    public RevertState Revert { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public string Notes { get; set; } =
        Loc.T("settings.notes");

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }

    public static string GeneratePassword(int length = 12)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    /// <summary>校验热点名/密码，返回当前界面语言的错误说明；合法时返回 null。</summary>
    public static string? Validate(string ssid, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return Loc.T("settings.hotspotNameCannotBeEmpty");
        if (Encoding.UTF8.GetByteCount(ssid) > 32) return Loc.T("settings.hotspotNameTooLongMax");
        if (password.Length < 8 || password.Length > 63) return Loc.T("settings.passwordMustBe863");
        foreach (var c in password)
            if (c < 0x20 || c > 0x7E) return Loc.T("settings.passwordMayOnlyContainEnglish");
        return null;
    }
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _lock = new();
    public string DataDirectory { get; }
    public string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public string LeasePath => Path.Combine(DataDirectory, "dhcp-leases.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public bool IsFirstRun { get; private set; }

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? ResolveDataDirectory();
    }

    /// <summary>优先放在 exe 旁边（方便拷给舍友）；不可写时退回 %ProgramData%。</summary>
    public static string ResolveDataDirectory()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (IsWritable(exeDir)) return exeDir;
        var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRAirBridgeHotspot");
        Directory.CreateDirectory(alt);
        return alt;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            AppSettings? s = null;
            if (File.Exists(ConfigPath))
            {
                try
                {
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath, Encoding.UTF8), JsonOptions);
                }
                catch (Exception ex)
                {
                    var bad = ConfigPath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Copy(ConfigPath, bad, true); } catch { }
                    Logger.Error(Loc.T("settings.configJsonMalformedBackedUp", ex.Message, Path.GetFileName(bad)));
                }
            }
            else IsFirstRun = true;
            s ??= new AppSettings();
            s.Dhcp ??= new DhcpSettings();
            s.Nat ??= new NatSettings();
            s.Internet ??= new InternetSettings();
            s.Channel ??= new ChannelSettings();
            s.Channel.PinSsid ??= "";
            s.Revert ??= new RevertState();
            s.Update ??= new UpdateSettings();
            s.Update.SkippedVersion ??= "";
            s.Ui ??= new UiSettings();
            if (string.IsNullOrWhiteSpace(s.Ui.Language)) s.Ui.Language = "auto";
            if (string.IsNullOrWhiteSpace(s.Ui.Theme)) s.Ui.Theme = "system";
            s.Revert.PinProfilesAdded ??= new List<string>();
            s.Revert.DisabledAdapters ??= new List<string>();
            s.Revert.ForwardingEnabledOn ??= new List<string>();
            s.Notes = new AppSettings().Notes;
            s.Dhcp.FallbackDns ??= new List<string>();
            if (s.WatchdogIntervalSeconds < 2 || s.WatchdogIntervalSeconds > 30) s.WatchdogIntervalSeconds = 4;
            if (s.Dhcp.LeaseHours <= 0) s.Dhcp.LeaseHours = 12;
            return s;
        }
    }

    public void Save(AppSettings s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, ConfigPath, true);
        }
    }
}

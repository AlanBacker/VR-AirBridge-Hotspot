namespace VRAirBridge.Core.Settings;

/// <summary>“是否允许连接的设备访问互联网”功能的所有用户可见文字（从字符串表读取，随界面语言切换）。</summary>
public static class InternetStrings
{
    public static string CheckBox => Loc.T("internet.checkBox");
    public static string Hint => Loc.T("internet.hint");
    public static string TrayItem => Loc.T("internet.trayItem");

    public static string Allowed => Loc.T("internet.allowed");
    public static string LanOnly => Loc.T("internet.lanOnly");
    public static string StatusText(bool allow) => Loc.T(allow ? "internet.statusAllowed" : "internet.statusLanOnly");

    public static string NatLanOnly => Loc.T("internet.natLanOnly");
    public static string NatLanOnlyNeedAdmin => Loc.T("internet.natLanOnlyNeedAdmin");
    public static string NatLanOnlyIcs => Loc.T("internet.natLanOnlyIcs");
    public static string DhcpLanOnlyNote => Loc.T("internet.dhcpLanOnlyNote");

    public static string AppliedOn => Loc.T("internet.appliedOn");
    public static string AppliedOff => Loc.T("internet.appliedOff");
    public static string SavedOnlyNotRunning => Loc.T("internet.savedOnlyNotRunning");
    public static string IcsHint => Loc.T("internet.icsHint");

    public static string CliUsage => Loc.T("internet.cliUsage");
    public static string CliSetOn => Loc.T("internet.cliSetOn");
    public static string CliSetOff => Loc.T("internet.cliSetOff");
    public static string CliSignalled => Loc.T("internet.cliSignalled");
    public static string CliAppliedDirect => Loc.T("internet.cliAppliedDirect");
    public static string CliNextStart => Loc.T("internet.cliNextStart");
    public static string CliCannotSignal => Loc.T("internet.cliCannotSignal");
}

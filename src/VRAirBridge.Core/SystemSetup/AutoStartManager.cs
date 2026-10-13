using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

/// <summary>
/// 开机自启动：用“任务计划程序”在用户登录时以最高权限启动（这样不会每次弹 UAC）。
/// </summary>
public static class AutoStartManager
{
    public const string TaskName = "VRAirBridgeHotspot";

    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    /// <summary>任务是否存在；exePath 返回任务里登记的程序路径。</summary>
    public static bool IsEnabled(out string? exePath)
    {
        exePath = null;
        try
        {
            // 重定向时 schtasks 按控制台代码页输出 XML
            var r = ProcessRunner.Run(SchTasks, $"/Query /TN \"{TaskName}\" /XML", 15000, ConsoleEncoding());
            if (!r.Success) return false;
            var m = Regex.Match(r.StdOut, "<Command>(.*?)</Command>", RegexOptions.Singleline);
            if (m.Success) exePath = SecurityElementUnescape(m.Groups[1].Value).Trim().Trim('"');
            return true;
        }
        catch { return false; }
    }

    /// <summary>控制台程序（schtasks、netsh 等）重定向输出时使用的 OEM 代码页（中文系统为 GBK/936）。</summary>
    public static Encoding ConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.UTF8; }
    }

    private static string SecurityElementUnescape(string s) =>
        s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    public static bool Enable(string exePath, out string message)
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().Name; }
        catch { user = Environment.UserDomainName + "\\" + Environment.UserName; }
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>VR Air Bridge Hotspot</Author>
    <Description>{SecurityElement.Escape(Loc.T("autostart.taskDescription"))}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{SecurityElement.Escape(exePath)}""</Command>
      <Arguments>--autostart</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        var tmp = Path.Combine(Path.GetTempPath(), $"VRAirBridge-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var r = ProcessRunner.Run(SchTasks, $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", 20000, ConsoleEncoding());
            message = r.Success ? Loc.T("autostart.startWithWindowsEnabledScheduled", TaskName) : Loc.T("autostart.failedEnableStartWithWindows") + r.Combined;
            return r.Success;
        }
        catch (Exception ex)
        {
            message = Loc.T("autostart.failedEnableStartWithWindows") + ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static bool Disable(out string message)
    {
        if (!IsEnabled(out _)) { message = Loc.T("autostart.startWithWindowsWasNot"); return true; }
        var r = ProcessRunner.Run(SchTasks, $"/Delete /TN \"{TaskName}\" /F", 20000, ConsoleEncoding());
        message = r.Success ? Loc.T("autostart.startWithWindowsDisabled") : Loc.T("autostart.failedDisableStartWithWindows") + r.Combined;
        return r.Success;
    }
}

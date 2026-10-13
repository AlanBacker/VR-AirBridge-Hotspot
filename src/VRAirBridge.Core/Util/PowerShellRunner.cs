using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VRAirBridge.Core.Util;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string Combined => (StdOut + "\n" + StdErr).Trim();
    public IEnumerable<string> Lines => StdOut.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}

/// <summary>运行外部程序 / Windows PowerShell 脚本（UTF-8 输出，带超时）。</summary>
public static class ProcessRunner
{
    public static ProcessResult Run(string file, string arguments, int timeoutMs = 60000, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (encoding != null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool exited = p.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { p.Kill(true); } catch { }
            return new ProcessResult(-1, so.ToString(), se.ToString(), true);
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, so.ToString(), se.ToString(), false);
    }

    public static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 用 -EncodedCommand 运行一段 PowerShell 脚本，避免引号/中文转义问题。
    /// 脚本里出错时请自己 Write-Output 'ERR:...' 并 exit 1。
    /// </summary>
    public static ProcessResult PowerShell(string script, int timeoutMs = 90000)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;\n" + script;
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return Run(PowerShellPath, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, new UTF8Encoding(false));
    }

    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    /// <summary>
    /// 控制台程序（pnputil、schtasks 等）输出用的 OEM 代码页（中文系统是 936），不是 UTF-8。
    /// 用 GetOEMCP 取系统值，取不到时退回当前区域的 OEM 代码页。
    /// </summary>
    public static Encoding OemEncoding()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch
        {
            try { return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    /// <summary>PowerShell 单引号字符串转义。</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}

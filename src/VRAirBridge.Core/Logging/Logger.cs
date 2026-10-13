using System.Collections.Concurrent;
using System.Text;

namespace VRAirBridge.Core.Logging;

public enum LogLevel { Debug, Info, Ok, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Debug => Loc.T("log.debug"),
        LogLevel.Info => Loc.T("log.info"),
        LogLevel.Ok => Loc.T("log.ok"),
        LogLevel.Warn => Loc.T("log.warning"),
        LogLevel.Error => Loc.T("log.error"),
        _ => "?"
    };

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Message}";
}

/// <summary>
/// 线程安全的日志：写入 logs\app-yyyyMMdd.log（UTF-8），按天滚动，单文件超过上限再按序号滚动，
/// 自动清理过期日志；同时通过事件推送给界面。
/// 密码等敏感字符串可以登记为 secret，写日志前会被替换成 ******。
/// </summary>
public static class Logger
{
    private static readonly object FileLock = new();
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static string? _dir;
    private static string? _currentPath;
    private static DateTime _currentDay;
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const int KeepDays = 14;

    public static event Action<LogEntry>? EntryWritten;

    public static string? LogDirectory => _dir;

    public static void Initialize(string logDirectory)
    {
        _dir = logDirectory;
        try
        {
            Directory.CreateDirectory(logDirectory);
            CleanupOld();
        }
        catch { /* 日志目录不可写时只推送到界面 */ }
    }

    /// <summary>登记敏感字符串（例如热点密码）；之后任何日志里出现都会被打码。</summary>
    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4) Secrets[secret] = 0;
    }

    public static void ForgetSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret)) Secrets.TryRemove(secret, out _);
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Ok(string msg) => Write(LogLevel.Ok, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Write(LogLevel level, string msg)
    {
        foreach (var s in Secrets.Keys)
            if (msg.Contains(s, StringComparison.Ordinal)) msg = msg.Replace(s, "******", StringComparison.Ordinal);
        var entry = new LogEntry(DateTime.Now, level, msg);
        WriteToFile(entry);
        try { EntryWritten?.Invoke(entry); } catch { }
    }

    private static void WriteToFile(LogEntry e)
    {
        if (_dir == null) return;
        lock (FileLock)
        {
            try
            {
                if (_currentPath == null || _currentDay != e.Time.Date)
                {
                    _currentDay = e.Time.Date;
                    _currentPath = Path.Combine(_dir, $"app-{e.Time:yyyyMMdd}.log");
                }
                var fi = new FileInfo(_currentPath);
                if (fi.Exists && fi.Length > MaxFileBytes)
                {
                    // 当天日志过大：滚动为 app-yyyyMMdd.1.log, .2.log …
                    for (int i = 1; i < 100; i++)
                    {
                        var rolled = Path.Combine(_dir, $"app-{e.Time:yyyyMMdd}.{i}.log");
                        if (!File.Exists(rolled)) { File.Move(_currentPath, rolled); break; }
                    }
                }
                bool isNew = !File.Exists(_currentPath);
                using var fs = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var sw = new StreamWriter(fs, new UTF8Encoding(isNew));
                sw.WriteLine(e.ToString());
            }
            catch { /* 忽略写日志失败 */ }
        }
    }

    private static void CleanupOld()
    {
        if (_dir == null) return;
        foreach (var f in Directory.EnumerateFiles(_dir, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
            }
            catch { }
        }
    }
}

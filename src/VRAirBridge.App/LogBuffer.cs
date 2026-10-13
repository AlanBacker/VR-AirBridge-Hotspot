using System.Collections.Concurrent;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.App;

/// <summary>保存最近的日志（界面打开前产生的日志也能显示），并把新日志排队给界面定时取走。</summary>
internal static class LogBuffer
{
    private const int Max = 4000;
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private static readonly ConcurrentQueue<LogEntry> Pending = new();
    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        Logger.EntryWritten += e =>
        {
            Recent.Enqueue(e);
            while (Recent.Count > Max && Recent.TryDequeue(out _)) { }
            Pending.Enqueue(e);
            while (Pending.Count > Max && Pending.TryDequeue(out _)) { }
        };
    }

    public static List<LogEntry> Snapshot() => Recent.ToList();

    public static List<LogEntry> DrainPending()
    {
        var list = new List<LogEntry>();
        while (Pending.TryDequeue(out var e)) list.Add(e);
        return list;
    }
}

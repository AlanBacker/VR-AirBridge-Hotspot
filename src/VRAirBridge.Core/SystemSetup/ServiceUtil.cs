using System.Diagnostics;
using System.Management;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.SystemSetup;

public sealed record ServiceInfo(string Name, string DisplayName, string State, string StartMode, int ProcessId);

/// <summary>Windows 服务的查询和控制（WMI Win32_Service）。控制操作需要管理员。</summary>
public static class ServiceUtil
{
    public static ServiceInfo? Get(string name)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT Name, DisplayName, State, StartMode, ProcessId FROM Win32_Service WHERE Name = '{name.Replace("'", "")}'");
            foreach (ManagementBaseObject o in s.Get())
                using (o)
                    return new ServiceInfo(o["Name"] as string ?? name, o["DisplayName"] as string ?? name, o["State"] as string ?? "",
                        o["StartMode"] as string ?? "", Convert.ToInt32(o["ProcessId"] ?? 0));
        }
        catch (Exception ex) { Logger.Debug(Loc.T("service.failedQueryService", name, ex.Message)); }
        return null;
    }

    private static uint Invoke(string name, string method, object[]? args = null)
    {
        using var s = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name = '{name.Replace("'", "")}'");
        foreach (ManagementObject o in s.Get())
            using (o)
                return Convert.ToUInt32(o.InvokeMethod(method, args) ?? 0u);
        throw new InvalidOperationException(Loc.T("service.serviceDoesnTExist", name));
    }

    public static bool SetStartMode(string name, string mode)
    {
        try { return Invoke(name, "ChangeStartMode", new object[] { mode }) == 0; }
        catch (Exception ex) { Logger.Warn(Loc.T("service.failedChangeStartTypeService", name, ex.Message)); return false; }
    }

    public static async Task<bool> WaitStateAsync(string name, string state, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (string.Equals(Get(name)?.State, state, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(500);
        }
        return string.Equals(Get(name)?.State, state, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>停止服务：最多尝试 3 次；仍停不下来且它的 svchost 进程只托管它一个服务时，强制结束该进程。</summary>
    public static async Task<bool> StopReliablyAsync(string name)
    {
        for (int i = 1; i <= 3; i++)
        {
            var info = Get(name);
            if (info == null) return true;
            if (string.Equals(info.State, "Stopped", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var rc = Invoke(name, "StopService");
                if (rc != 0 && rc != 5) Logger.Debug(Loc.T("service.stoppingAttemptReturned", name, i, rc));
            }
            catch (Exception ex) { Logger.Warn(Loc.T("service.stoppingAttemptFailed", name, i, ex.Message)); }
            if (await WaitStateAsync(name, "Stopped", TimeSpan.FromSeconds(10))) return true;
            Logger.Warn(Loc.T("service.still", name, Get(name)?.State));
        }
        var svc = Get(name);
        if (svc != null && svc.ProcessId > 0)
        {
            var others = new List<string>();
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT Name FROM Win32_Service WHERE ProcessId = {svc.ProcessId}");
                foreach (ManagementBaseObject o in s.Get())
                    using (o)
                    {
                        var n = o["Name"] as string ?? "";
                        if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) others.Add(n);
                    }
            }
            catch { others.Add("?"); }
            if (others.Count == 0)
            {
                Logger.Warn(Loc.T("service.wonTStopEndingIts", name, svc.ProcessId));
                try { Process.GetProcessById(svc.ProcessId).Kill(); }
                catch (Exception ex) { Logger.Error(Loc.T("service.failedEndProcess") + ex.Message); }
                if (await WaitStateAsync(name, "Stopped", TimeSpan.FromSeconds(10))) return true;
            }
            else Logger.Error(Loc.T("service.processAlsoHostsOtherServices", name, string.Join(", ", others)));
        }
        return string.Equals(Get(name)?.State, "Stopped", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> StartAsync(string name)
    {
        try
        {
            var info = Get(name);
            if (info == null) return false;
            if (string.Equals(info.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase)) SetStartMode(name, "Manual");
            if (!string.Equals(info.State, "Running", StringComparison.OrdinalIgnoreCase)) Invoke(name, "StartService");
            return await WaitStateAsync(name, "Running", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Logger.Warn(Loc.T("service.failedStartService", name, ex.Message));
            return false;
        }
    }
}

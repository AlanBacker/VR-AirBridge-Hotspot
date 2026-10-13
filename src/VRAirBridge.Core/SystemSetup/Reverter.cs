using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

/// <summary>
/// “还原 / 卸载”：撤销本软件对系统做过的改动——删除 WinNAT、关闭打开过的 IPv4 转发、删除“钉住信道”复制到 D-Link 的 Wi-Fi 配置并恢复跃点数、
/// 重新启用禁用过的网卡、把 OVRService 改回自动、删除防火墙规则和开机自启动任务。
/// 热点本身（Windows 移动热点的名称/密码）和“无连接自动关闭”的注册表设置保持不变。
/// </summary>
public static class Reverter
{
    public static async Task<List<string>> RunAsync(AppSettings s, Action save)
    {
        var report = new List<string>();
        if (!AdminUtil.IsAdministrator())
        {
            report.Add(Loc.T("revert.administratorRightsAreRequiredRevert"));
            return report;
        }

        report.AddRange(NatManager.Revert(s));
        report.AddRange(ChannelPin.Revert(s));

        foreach (var name in s.Revert.DisabledAdapters.ToList())
        {
            report.Add(IcsRepair.SetAdapterEnabled(name, true) ? Loc.T("revert.reEnabled", name) : Loc.T("revert.failedReEnableYouCan", name));
            s.Revert.DisabledAdapters.Remove(name);
        }

        if (s.Revert.OvrServiceChangedToManual)
        {
            report.Add(ServiceUtil.SetStartMode("OVRService", "Automatic") ? Loc.T("revert.setMetaSOvrserviceBack") : Loc.T("revert.failedSetOvrserviceBackAutomatic"));
            s.Revert.OvrServiceChangedToManual = false;
        }

        report.AddRange(FirewallManager.RemoveRules());

        if (AutoStartManager.IsEnabled(out _))
        {
            AutoStartManager.Disable(out var msg);
            report.Add(msg);
        }
        s.AutoStart = false;
        save();
        await Task.CompletedTask;
        if (report.Count == 0) report.Add(Loc.T("revert.nothingRevert"));
        foreach (var r in report) Logger.Info(Loc.T("revert.revert") + r);
        return report;
    }
}

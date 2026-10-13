using System.Management;
using Microsoft.Win32;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Logging;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Util;

namespace VRAirBridge.Core.SystemSetup;

public sealed record StepResult(LogLevel Level, string Text);

/// <summary>
/// “一键优化”：一次性的系统设置（需要管理员），让 D-Link 热点满血、不省电、不自动关闭。
/// 每一步都可重复执行；已经是正确状态的步骤只报告不修改。
/// </summary>
public static class SystemOptimizer
{
    private const string IcsSettingsKey = @"SYSTEM\CurrentControlSet\Services\icssvc\Settings";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb50f7e206";
    private const string WirelessSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WirelessPowerMode = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>“无设备连接时自动关闭热点”是否已经关掉（读注册表，不需要管理员）。</summary>
    public static bool IsPeerlessTimeoutDisabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(IcsSettingsKey);
            return k?.GetValue("PeerlessTimeoutEnabled") is int v && v == 0;
        }
        catch { return false; }
    }

    public static async Task<List<StepResult>> RunAsync(DongleInfo? dongle, string exePath, AppSettings settings, Action save, Func<string, bool> confirm)
    {
        var res = new List<StepResult>();
        void Add(LogLevel l, string t) { res.Add(new StepResult(l, t)); Logger.Write(l, Loc.T("optimize.oneClickOptimize") + t); }
        void AddR(StepResult r) => Add(r.Level, r.Text);

        if (!AdminUtil.IsAdministrator())
        {
            Add(LogLevel.Error, Loc.T("optimize.administratorRightsRequiredRightClick"));
            return res;
        }

        // 1. 热点无人连接时自动关闭
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(IcsSettingsKey, true);
            if (k.GetValue("PeerlessTimeoutEnabled") is int v && v == 0) Add(LogLevel.Ok, Loc.T("optimize.turnOffHotspotWhenNo"));
            else { k.SetValue("PeerlessTimeoutEnabled", 0, RegistryValueKind.DWord); Add(LogLevel.Ok, Loc.T("optimize.disabledTurnOffHotspotWhen")); }
        }
        catch (Exception ex) { Add(LogLevel.Error, Loc.T("optimize.failedSetPeerlesstimeoutenabled") + ex.Message); }
        try
        {
            if (HotspotController.IsNoConnectionsTimeoutEnabled() == true)
                Add(await HotspotController.DisableNoConnectionsTimeoutAsync() ? LogLevel.Ok : LogLevel.Warn,
                    Loc.T("optimize.systemApiTurnOffNo", HotspotController.IsNoConnectionsTimeoutEnabled() == false ? Loc.T("optimize.ok") : Loc.T("optimize.notAppliedRegistrySettingStill")));
        }
        catch { }

        // 2. D-Link 电源管理
        if (dongle == null)
        {
            Add(LogLevel.Warn, Loc.T("optimize.noDLinkAdapterDetected"));
        }
        else
        {
            try
            {
                int changed = 0, found = 0;
                using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT * FROM MSPower_DeviceEnable"));
                foreach (ManagementObject o in s.Get())
                {
                    using (o)
                    {
                        var inst = o["InstanceName"] as string ?? "";
                        if (!inst.StartsWith(dongle.PnpId, StringComparison.OrdinalIgnoreCase)) continue;
                        found++;
                        if (o["Enable"] is bool b && b) { o["Enable"] = false; o.Put(); changed++; }
                    }
                }
                if (found == 0) Add(LogLevel.Warn, Loc.T("optimize.dLinkPowerManagementEntry"));
                else Add(LogLevel.Ok, changed > 0 ? Loc.T("optimize.windowsMayNoLongerTurn") : Loc.T("optimize.dLinkAdapterAlreadySet"));
            }
            catch (Exception ex) { Add(LogLevel.Warn, Loc.T("optimize.failedSetDLinkPower") + ex.Message); }

            var r = ProcessRunner.PowerShell(
                $"try {{ Disable-NetAdapterPowerManagement -Name {ProcessRunner.Q(dongle.Name)} -NoRestart -ErrorAction Stop; 'OK' }} catch {{ 'ERR:' + $_.Exception.Message; exit 1 }}", 60000);
            Add(r.Success ? LogLevel.Ok : LogLevel.Warn, r.Success ? Loc.T("optimize.turnedOffAdapterSOwn") : Loc.T("optimize.failedTurnOffAdapterPower") + r.Combined.Replace("ERR:", ""));

            // 驱动高级属性：节能关闭、无线模式包含 ax
            var props = ReadAdvanced(dongle);
            var fixes = new List<string>();
            foreach (var kw in new[] { "bLeisurePs", "InactivePs" })
                if (props.TryGetValue(kw, out var v) && v != "0") fixes.Add($"Set-NetAdapterAdvancedProperty -Name {ProcessRunner.Q(dongle.Name)} -RegistryKeyword {kw} -RegistryValue 0 -NoRestart -ErrorAction Stop");
            if (props.TryGetValue("WifiProtocol_phy0", out var proto) && proto != "63")
                fixes.Add($"Set-NetAdapterAdvancedProperty -Name {ProcessRunner.Q(dongle.Name)} -RegistryKeyword WifiProtocol_phy0 -RegistryValue 63 -NoRestart -ErrorAction Stop");
            if (fixes.Count == 0)
            {
                Add(LogLevel.Ok, Loc.T("optimize.dLinkDriverSettingsAre"));
            }
            else
            {
                var fr = ProcessRunner.PowerShell("try { " + string.Join("; ", fixes) + "; 'OK' } catch { 'ERR:' + $_.Exception.Message; exit 1 }", 60000);
                Add(fr.Success ? LogLevel.Ok : LogLevel.Warn, fr.Success
                    ? Loc.T("optimize.setDLinkDriverFull")
                    : Loc.T("optimize.failedChangeDLinkDriver") + fr.Combined.Replace("ERR:", ""));
            }
        }

        // 3. 电源方案：USB 选择性暂停、无线网卡节能
        AddR(PowerCfg(UsbSubgroup, UsbSelectiveSuspend, Loc.T("optimize.usbSelectiveSuspend")));
        AddR(PowerCfg(WirelessSubgroup, WirelessPowerMode, Loc.T("optimize.wirelessAdapterPowerSavingSet")));
        ProcessRunner.Run("powercfg.exe", "/setactive SCHEME_CURRENT", 15000);

        // 4. 防火墙
        var fw = FirewallManager.EnsureRules(exePath);
        var check = FirewallManager.Check(exePath);
        if (check.AllOk) Add(LogLevel.Ok, Loc.T("optimize.windowsFirewallAllowsAppUdp") + (fw.Count > 0 ? Loc.T("common.colon") + string.Join(Loc.T("common.listSep"), fw) : ""));
        else Add(LogLevel.Warn, Loc.T("optimize.firewallRulesAreNotFully") + string.Join(Loc.T("common.listSep"), fw) + (check.Error ?? ""));

        // 5. Meta 的 OVRService
        var ovr = ServiceUtil.Get("OVRService");
        if (ovr == null) Add(LogLevel.Info, Loc.T("optimize.metaSOvrserviceNotInstalled"));
        else if (string.Equals(ovr.StartMode, "Auto", StringComparison.OrdinalIgnoreCase) || string.Equals(ovr.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            if (confirm(Loc.T("optimize.foundMetaSBackgroundService", ovr.State, ovr.StartMode)))
            {
                bool okMode = ServiceUtil.SetStartMode("OVRService", "Manual");
                bool okStop = await ServiceUtil.StopReliablyAsync("OVRService");
                if (okMode && string.Equals(ovr.StartMode, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    settings.Revert.OvrServiceChangedToManual = true;
                    save();
                }
                Add(okMode && okStop ? LogLevel.Ok : LogLevel.Warn, Loc.T("optimize.ovrserviceStartTypeSetManual", (okMode ? Loc.T("optimize.ok") : Loc.T("optimize.failed")), (okStop ? Loc.T("optimize.ok") : Loc.T("optimize.failed"))));
            }
            else Add(LogLevel.Info, Loc.T("optimize.leftOvrserviceUnchanged"));
        }
        else Add(LogLevel.Ok, Loc.T("optimize.metaSOvrserviceAlreadyManual"));

        Add(LogLevel.Ok, Loc.T("optimize.oneClickOptimizeFinished"));
        return res;
    }

    private static StepResult PowerCfg(string sub, string setting, string what)
    {
        var a = ProcessRunner.Run("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {sub} {setting} 0", 15000);
        var d = ProcessRunner.Run("powercfg.exe", $"/setdcvalueindex SCHEME_CURRENT {sub} {setting} 0", 15000);
        if (a.Success && d.Success) return new StepResult(LogLevel.Ok, Loc.T("optimize.powerPlanTurnedOffPlugged", what));
        return new StepResult(LogLevel.Info, Loc.T("optimize.powerPlanHasNoSetting", what));
    }

    /// <summary>读取网卡驱动高级属性（包括隐藏项）：关键字 → 注册表值。</summary>
    public static Dictionary<string, string> ReadAdvanced(DongleInfo d)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var desc = d.Description.Replace("'", "''");
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"), new ObjectQuery(
                $"SELECT RegistryKeyword, RegistryValue FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE InterfaceDescription = '{desc}'"));
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o)
                {
                    var kw = o["RegistryKeyword"] as string;
                    if (kw == null) continue;
                    var v = o["RegistryValue"] is string[] arr ? string.Join(",", arr) : o["RegistryValue"]?.ToString() ?? "";
                    res[kw] = v;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(Loc.T("optimize.failedReadAdapterSAdvanced") + ex.Message); }
        return res;
    }
}

using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.SystemSetup;

/// <summary>
/// Windows 防火墙规则（通过 HNetCfg.FwPolicy2 COM 接口）。
/// 规则按名称识别，重复执行不会重复添加；exe 换了位置会自动更新路径。
/// </summary>
public static class FirewallManager
{
    public const string DhcpRuleName = "VRAirBridge-DHCP-In";
    public const string AppRuleName = "VRAirBridge-App-In";
    public const string Grouping = "VR Air Bridge Hotspot";

    private const int DirIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtoUdp = 17;
    private const int ProtoAny = 256;
    private const int ProfilesAll = 0x7FFFFFFF;

    private static dynamic Policy()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException(Loc.T("firewall.windowsFirewallComInterfaceNot"));
        return Activator.CreateInstance(t)!;
    }

    private static dynamic? Find(dynamic policy, string name)
    {
        try { return policy.Rules.Item(name); }
        catch { return null; }
    }

    public sealed record FirewallCheck(bool DhcpRuleOk, bool AppRuleOk, List<string> BlockingRules, string? Error)
    {
        public bool AllOk => DhcpRuleOk && AppRuleOk && BlockingRules.Count == 0;
    }

    /// <summary>检查规则是否存在且指向当前 exe；同时找出“阻止本程序”的入站规则（用户在防火墙弹窗里点了取消就会生成）。</summary>
    public static FirewallCheck Check(string exePath)
    {
        try
        {
            var policy = Policy();
            bool Ok(string name)
            {
                var r = Find(policy, name);
                if (r == null) return false;
                return (bool)r.Enabled && (int)r.Action == ActionAllow &&
                       string.Equals((string?)r.ApplicationName, exePath, StringComparison.OrdinalIgnoreCase);
            }
            var blocking = new List<string>();
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    if ((int)r.Direction == DirIn && (int)r.Action == ActionBlock && (bool)r.Enabled &&
                        string.Equals((string?)r.ApplicationName, exePath, StringComparison.OrdinalIgnoreCase))
                        blocking.Add((string)r.Name);
                }
                catch { }
            }
            return new FirewallCheck(Ok(DhcpRuleName), Ok(AppRuleName), blocking, null);
        }
        catch (Exception ex)
        {
            return new FirewallCheck(false, false, new List<string>(), ex.Message);
        }
    }

    /// <summary>Program paths in our rules: where the exe was when the rules were last updated. Empty when there are none.</summary>
    public static List<string> RecordedExePaths()
    {
        var paths = new List<string>();
        try
        {
            var policy = Policy();
            foreach (var name in new[] { AppRuleName, DhcpRuleName })
            {
                var r = Find(policy, name);
                if (r == null) continue;
                string? path = r.ApplicationName;
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
        }
        catch { }
        return paths;
    }

    /// <summary>添加/更新放行规则，删除阻止本程序的入站规则（需要管理员）。返回做了哪些改动。</summary>
    public static List<string> EnsureRules(string exePath)
    {
        var done = new List<string>();
        try
        {
            var policy = Policy();
            Upsert(policy, DhcpRuleName, Loc.T("firewall.allowsBuiltDhcpServerVr"), exePath, ProtoUdp, "67", done);
            Upsert(policy, AppRuleName, Loc.T("firewall.allowsVrAirBridgeHotspot"), exePath, ProtoAny, null, done);

            var blocking = new List<string>();
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    if ((int)r.Direction == DirIn && (int)r.Action == ActionBlock &&
                        string.Equals((string?)r.ApplicationName, exePath, StringComparison.OrdinalIgnoreCase))
                        blocking.Add((string)r.Name);
                }
                catch { }
            }
            foreach (var name in blocking.Distinct())
            {
                // Remove 按名字删除；名字相同的规则可能有多条，循环删到没有为止（最多 10 次）
                for (int i = 0; i < 10; i++)
                {
                    var r = Find(policy, name);
                    if (r == null || (int)r!.Action != ActionBlock) break;
                    policy.Rules.Remove(name);
                }
                done.Add(Loc.T("firewall.removedFirewallRuleThatBlocked", name));
            }
        }
        catch (Exception ex)
        {
            done.Add(Loc.T("firewall.failedConfigureFirewall") + ex.Message);
            Logger.Warn(Loc.T("firewall.failedConfigureFirewall") + ex.Message);
        }
        foreach (var d in done) Logger.Info(Loc.T("firewall.firewall") + d);
        return done;
    }

    private static void Upsert(dynamic policy, string name, string desc, string exe, int proto, string? localPorts, List<string> done)
    {
        var r = Find(policy, name);
        if (r != null)
        {
            bool changed = false;
            if (!string.Equals((string?)r.ApplicationName, exe, StringComparison.OrdinalIgnoreCase)) { r.ApplicationName = exe; changed = true; }
            if (!(bool)r.Enabled) { r.Enabled = true; changed = true; }
            if ((int)r.Action != ActionAllow) { r.Action = ActionAllow; changed = true; }
            if ((int)r.Profiles != ProfilesAll) { r.Profiles = ProfilesAll; changed = true; }
            if (changed) done.Add(Loc.T("firewall.updatedRuleProgramPath", name, exe));
            return;
        }
        var t = Type.GetTypeFromProgID("HNetCfg.FWRule") ?? throw new InvalidOperationException(Loc.T("firewall.cannotCreateFirewallRuleObject"));
        dynamic rule = Activator.CreateInstance(t)!;
        rule.Name = name;
        rule.Description = desc;
        rule.ApplicationName = exe;
        rule.Protocol = proto;
        if (localPorts != null) rule.LocalPorts = localPorts;
        rule.Direction = DirIn;
        rule.Action = ActionAllow;
        rule.Enabled = true;
        rule.Profiles = ProfilesAll;
        rule.Grouping = Grouping;
        rule.InterfaceTypes = "All";
        policy.Rules.Add(rule);
        done.Add(Loc.T("firewall.addedRule", name));
    }

    public static List<string> RemoveRules()
    {
        var done = new List<string>();
        try
        {
            var policy = Policy();
            foreach (var name in new[] { DhcpRuleName, AppRuleName })
            {
                if (Find(policy, name) == null) continue;
                for (int i = 0; i < 10 && Find(policy, name) != null; i++) policy.Rules.Remove(name);
                done.Add(Loc.T("firewall.deletedFirewallRule", name));
            }
        }
        catch (Exception ex) { done.Add(Loc.T("firewall.failedDeleteFirewallRules") + ex.Message); }
        return done;
    }
}

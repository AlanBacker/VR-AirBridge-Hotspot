using System.Runtime.InteropServices;
using System.Text;

namespace VRAirBridge.Core.Util;

/// <summary>设备树（cfgmgr32）：按实例 ID 找设备、上级、下级、属性和是否已启动。不需要管理员。</summary>
internal static class DevNode
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Child(out uint child, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int len, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref int size, uint flags);

    private const uint DN_STARTED = 0x8;

    internal static readonly DEVPROPKEY BusReportedDesc = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    internal static readonly DEVPROPKEY FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    internal static readonly DEVPROPKEY DeviceDesc = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
    internal static readonly DEVPROPKEY Service = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 6 };

    /// <summary>按实例 ID 找到设备（只找当前存在的）。</summary>
    internal static uint? Locate(string? instanceId) =>
        !string.IsNullOrEmpty(instanceId) && CM_Locate_DevNodeW(out var d, instanceId, 0) == 0 ? d : null;

    internal static uint? Parent(uint dev) => CM_Get_Parent(out var p, dev, 0) == 0 ? p : null;

    internal static IEnumerable<uint> Children(uint parent)
    {
        if (CM_Get_Child(out var c, parent, 0) != 0) yield break;
        int guard = 0;
        do { yield return c; } while (++guard < 64 && CM_Get_Sibling(out c, c, 0) == 0);
    }

    internal static string Id(uint dev)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(dev, buf, buf.Length, 0) == 0 ? new string(buf).TrimEnd('\0') : "";
    }

    /// <summary>驱动已加载、设备在工作（被停用的、出错的都不算）。</summary>
    internal static bool IsStarted(uint dev) =>
        CM_Get_DevNode_Status(out var st, out _, dev, 0) == 0 && (st & DN_STARTED) != 0;

    internal static string StringProp(uint dev, DEVPROPKEY key)
    {
        int size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size <= 2) return "";
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) != 0) return "";
        return Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0').Trim();
    }

    /// <summary>给人看的设备名：总线报告的名字 → 友好名 → 设备描述。</summary>
    internal static string Name(uint dev)
    {
        foreach (var k in new[] { BusReportedDesc, FriendlyName, DeviceDesc })
        {
            var s = StringProp(dev, k);
            if (s.Length > 0) return s;
        }
        return "";
    }
}

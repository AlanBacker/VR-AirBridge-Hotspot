using Microsoft.Win32;

namespace VRAirBridge.Core.Update;

/// <summary>
/// The entry the setup (installer\VRAirBridgeHotspot.iss, Inno Setup AppId "VRAirBridgeHotspot") writes for
/// Apps &amp; features. The installed copy updates itself like the portable exe (the app runs as administrator, so it
/// can replace its file in Program Files); afterwards the version shown there is brought up to date.
/// </summary>
public static class InstallInfo
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VRAirBridgeHotspot_is1";

    /// <summary>True when <paramref name="exePath"/> lives in the folder the setup installed to.</summary>
    public static bool IsInstalled(string exePath)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
            return SameFolder(k?.GetValue("InstallLocation") as string, Path.GetDirectoryName(exePath));
        }
        catch { return false; }
    }

    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            static string N(string p) => Path.GetFullPath(p.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes <paramref name="version"/> as DisplayVersion when this is the installed copy and the entry shows another
    /// one (after a self-update). Needs administrator rights; returns true when it changed the entry.
    /// </summary>
    public static bool SyncDisplayVersion(string exePath, string version)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UninstallKey, writable: true);
            if (k == null || !SameFolder(k.GetValue("InstallLocation") as string, Path.GetDirectoryName(exePath))) return false;
            if (string.Equals(k.GetValue("DisplayVersion") as string, version, StringComparison.Ordinal)) return false;
            k.SetValue("DisplayVersion", version, RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }
}

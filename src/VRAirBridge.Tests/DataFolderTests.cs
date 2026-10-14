using VRAirBridge.Core;
using VRAirBridge.Core.Settings;
using VRAirBridge.Core.Update;
using Xunit;

namespace VRAirBridge.Tests;

public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vrab-migrate-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _old, _data;
    private static readonly string[] DriverFiles = { "netrtwlanufb.inf", "rtwlanufb.sys" };

    // demo values only
    private const string OurConfig = "{\"ssid\": \"AirBridge-TEST\", \"password\": \"demo-password-1\", \"dhcp\": {}, \"revert\": {}}";

    public LegacyDataMigrationTests()
    {
        _old = Path.Combine(_root, "portable");
        _data = Path.Combine(_root, "LocalAppData", SettingsStore.FolderName);
        Directory.CreateDirectory(_old);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Put(string dir, string rel, string text = "x")
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private bool Has(string dir, string rel) => File.Exists(Path.Combine(dir, rel));

    [Fact]
    public void Moves_OnlyOurFiles()
    {
        Put(_old, "config.json", OurConfig);
        Put(_old, "dhcp-leases.json", "[]");
        Put(_old, "config.json.bad-20261001120000");
        Put(_old, "diag-20261001-120000.txt");
        Put(_old, @"logs\app-20261013.log");
        Put(_old, @"logs\notes.log");
        Put(_old, @"driver\netrtwlanufb.inf");
        Put(_old, @"driver\rtwlanufb.sys");
        Put(_old, "VRAirBridgeHotspot.exe");
        Put(_old, "readme.txt");

        var r = LegacyDataMigration.Run(_data, new[] { _old }, DriverFiles);

        Assert.NotNull(r);
        Assert.Equal(_old, r!.From);
        Assert.Empty(r.Failed);
        Assert.Equal(7, r.Moved); // 5 moved + 2 driver files deleted
        foreach (var f in new[] { "config.json", "dhcp-leases.json", "config.json.bad-20261001120000", "diag-20261001-120000.txt", @"logs\app-20261013.log" })
        {
            Assert.True(Has(_data, f), f);
            Assert.False(Has(_old, f), f);
        }
        Assert.Equal(OurConfig, File.ReadAllText(Path.Combine(_data, "config.json")));
        Assert.False(Directory.Exists(Path.Combine(_old, "driver")));
        // anything else stays where it is
        Assert.True(Has(_old, "VRAirBridgeHotspot.exe"));
        Assert.True(Has(_old, "readme.txt"));
        Assert.True(Has(_old, @"logs\notes.log"));
        Assert.False(Has(_data, @"logs\notes.log"));

        // once the data folder has config.json, nothing runs again
        Put(_old, "config.json", OurConfig);
        Assert.Null(LegacyDataMigration.Run(_data, new[] { _old }, DriverFiles));
        Assert.True(Has(_old, "config.json"));
    }

    [Fact]
    public void Skips_WhenDataFolderHasConfig()
    {
        Put(_old, "config.json", OurConfig);
        Put(_old, @"logs\app-20261013.log");
        Put(_data, "config.json", OurConfig);
        Assert.Null(LegacyDataMigration.Run(_data, new[] { _old }, DriverFiles));
        Assert.Null(LegacyDataMigration.FindOldConfig(_data, new[] { _old }));
        Assert.True(Has(_old, "config.json"));
        Assert.True(Has(_old, @"logs\app-20261013.log"));
    }

    [Theory]
    [InlineData("{\"name\": \"other app\", \"version\": 2}")]
    [InlineData("{\"ssid\": \"x\", \"dhcp\": {}}")]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    public void Skips_ConfigOfAnotherApp(string json)
    {
        Put(_old, "config.json", json);
        Put(_old, "dhcp-leases.json", "[]");
        Assert.Null(LegacyDataMigration.Run(_data, new[] { _old }, DriverFiles));
        Assert.True(Has(_old, "config.json"));
        Assert.True(Has(_old, "dhcp-leases.json"));
        Assert.False(Directory.Exists(_data));
    }

    [Fact]
    public void TakesFirstFolderWithOurConfig_AndKeepsExistingFiles()
    {
        var other = Path.Combine(_root, "ProgramData", SettingsStore.FolderName);
        Put(_old, "readme.txt");
        Put(other, "config.json", OurConfig);
        Put(other, @"logs\app-20261013.log", "old");
        Put(_data, @"logs\app-20261013.log", "new");

        Assert.Equal(Path.Combine(other, "config.json"), LegacyDataMigration.FindOldConfig(_data, new[] { _old, other, _data }));
        var r = LegacyDataMigration.Run(_data, new[] { _old, other }, DriverFiles);

        Assert.Equal(other, r!.From);
        Assert.Equal(1, r.Moved);
        Assert.True(Has(_data, "config.json"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_data, @"logs\app-20261013.log")));
        Assert.True(Has(other, @"logs\app-20261013.log")); // not overwritten, left in place
    }

    [Fact]
    public void IgnoresTheDataFolderItself()
    {
        Assert.Null(LegacyDataMigration.FindOldConfig(_data, new[] { _data + "\\", "" }));
        Assert.Null(LegacyDataMigration.Run(_data, new[] { _data }, DriverFiles));
    }

    [Fact]
    public void Candidates_ExeFolderFirst()
    {
        var c = LegacyDataMigration.Candidates(_old).ToList();
        Assert.Equal(2, c.Count);
        Assert.Equal(_old, c[0]);
        Assert.EndsWith(@"\" + SettingsStore.FolderName, c[1]);

        // recorded exe paths come in between; blank, relative and quoted values are handled
        var other = Path.Combine(_root, "Desktop", "VRAirBridgeHotspot");
        c = LegacyDataMigration.Candidates(_old, () => new[] { "", "VRAirBridgeHotspot.exe", "\"" + Path.Combine(other, AppInfo.ExeName) + "\"" }).ToList();
        Assert.Equal(new[] { _old, other }, c.Take(2));
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public void InstalledCopy_FindsThePortableFolder_OnlyWhenNeeded()
    {
        // nothing next to the installed exe, but the autostart task / firewall rules still name the portable one
        var installed = Path.Combine(_root, "Program Files", "VR Air Bridge Hotspot");
        Put(_old, "config.json", OurConfig);
        int lookups = 0;
        IEnumerable<string> Recorded()
        {
            lookups++;
            return new[] { Path.Combine(_old, AppInfo.ExeName) };
        }

        var r = LegacyDataMigration.Run(_data, LegacyDataMigration.Candidates(installed, Recorded), DriverFiles);
        Assert.Equal(_old, r!.From);
        Assert.Equal(OurConfig, File.ReadAllText(Path.Combine(_data, "config.json")));
        Assert.Equal(1, lookups);

        // the data folder has config.json now: the recorded paths are not even looked up
        Assert.Null(LegacyDataMigration.Run(_data, LegacyDataMigration.Candidates(installed, Recorded), DriverFiles));
        Assert.Null(LegacyDataMigration.FindOldConfig(_data, LegacyDataMigration.Candidates(installed, Recorded)));
        Assert.Equal(1, lookups);
    }
}

public class InstallInfoTests
{
    [Theory]
    [InlineData(@"C:\Program Files\VR Air Bridge Hotspot\", @"C:\Program Files\VR Air Bridge Hotspot", true)]
    [InlineData(@"c:\program files\vr air bridge hotspot", @"C:\Program Files\VR Air Bridge Hotspot\", true)]
    [InlineData("\"C:\\Program Files\\VR Air Bridge Hotspot\\\"", @"C:\Program Files\VR Air Bridge Hotspot", true)]
    [InlineData(@"C:\Program Files\VR Air Bridge Hotspot\", @"C:\Users\Player\Desktop", false)]
    [InlineData(null, @"C:\Program Files\VR Air Bridge Hotspot", false)]
    [InlineData("", @"C:\Program Files\VR Air Bridge Hotspot", false)]
    public void SameFolder(string? installLocation, string exeDir, bool expected) =>
        Assert.Equal(expected, InstallInfo.SameFolder(installLocation, exeDir));
}

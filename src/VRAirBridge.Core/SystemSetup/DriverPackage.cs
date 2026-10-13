using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRAirBridge.Core.SystemSetup;

public sealed record DriverManifestFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("length")] long Length);

/// <summary>driver-manifest.json：内置驱动包里每个文件的名字、SHA-256、长度。</summary>
public sealed record DriverManifest(
    [property: JsonPropertyName("driverVersion")] string DriverVersion,
    [property: JsonPropertyName("driverDate")] string DriverDate,
    [property: JsonPropertyName("inf")] string Inf,
    [property: JsonPropertyName("files")] IReadOnlyList<DriverManifestFile> Files)
{
    public static DriverManifest Parse(string json)
    {
        var m = JsonSerializer.Deserialize<DriverManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new DriverPackageException(Loc.T("driverPackage.driverManifestJsonEmpty"));
        if (m.Files == null || m.Files.Count == 0) throw new DriverPackageException(Loc.T("driverPackage.driverManifestJsonListsNo"));
        if (string.IsNullOrWhiteSpace(m.Inf)) throw new DriverPackageException(Loc.T("driverPackage.driverManifestJsonHasNo"));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Name) || f.Name != Path.GetFileName(f.Name) || f.Name.Contains("..") || f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new DriverPackageException(Loc.T("driverPackage.invalidFileNameDriverManifest", f.Name));
            if (f.Sha256 == null || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit))
                throw new DriverPackageException(Loc.T("driverPackage.invalidSha256DriverManifest", f.Name));
            if (f.Length < 0) throw new DriverPackageException(Loc.T("driverPackage.invalidLengthDriverManifestJson", f.Name));
            if (!names.Add(f.Name)) throw new DriverPackageException(Loc.T("driverPackage.listedTwiceDriverManifestJson", f.Name));
        }
        if (!names.Contains(m.Inf)) throw new DriverPackageException(Loc.T("driverPackage.fileListDriverManifestJson", m.Inf));
        return m;
    }
}

public sealed class DriverPackageException : Exception
{
    public DriverPackageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 内置驱动包。Core 不直接引用界面程序的资源：由调用方提供“按文件名打开流”的函数
/// （界面程序用嵌入资源 VRAirBridge.App.driver.&lt;文件名&gt;，测试用目录或测试程序集）。
/// </summary>
public sealed class DriverPackage
{
    public const string ManifestName = "driver-manifest.json";
    public const string ResourcePrefix = "VRAirBridge.App.driver.";

    private readonly Func<string, Stream?> _open;
    private DriverManifest? _manifest;

    public DriverPackage(Func<string, Stream?> open) => _open = open;

    public static DriverPackage FromAssembly(Assembly asm, string prefix = ResourcePrefix) =>
        new(name => asm.GetManifestResourceStream(prefix + name));

    public static DriverPackage FromDirectory(string dir) =>
        new(name => { var p = Path.Combine(dir, name); return File.Exists(p) ? File.OpenRead(p) : null; });

    public DriverManifest Manifest
    {
        get
        {
            if (_manifest != null) return _manifest;
            using var s = _open(ManifestName) ?? throw new DriverPackageException(Loc.T("driverPackage.bundledDriverManifestJsonNot"));
            using var r = new StreamReader(s);
            return _manifest = DriverManifest.Parse(r.ReadToEnd());
        }
    }

    /// <summary>不解压，直接核对每个内置文件的长度和 SHA-256。返回问题列表（空 = 全部正确）。</summary>
    public List<string> Verify()
    {
        var problems = new List<string>();
        DriverManifest m;
        try { m = Manifest; }
        catch (Exception ex) { problems.Add(ex.Message); return problems; }
        foreach (var f in m.Files)
        {
            using var s = _open(f.Name);
            if (s == null) { problems.Add(Loc.T("driverPackage.missing", f.Name)); continue; }
            var (hash, len) = Hash(s);
            if (len != f.Length) problems.Add(Loc.T("driverPackage.hasLengthExpected", f.Name, len, f.Length));
            else if (!string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add(Loc.T("driverPackage.sha256DoesnTMatch", f.Name));
        }
        return problems;
    }

    /// <summary>
    /// 解压到 targetDir 并核对 SHA-256。先写到同级的临时目录、逐个读回校验，全部通过后才替换 targetDir；
    /// 任何一步失败都会删掉临时目录，不会留下只解压了一半的文件。返回文件数。
    /// </summary>
    public int ExtractTo(string targetDir)
    {
        var m = Manifest;
        targetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(targetDir) ?? throw new DriverPackageException(Loc.T("driverPackage.invalidExtractionFolder") + targetDir);
        Directory.CreateDirectory(parent);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var tmp = Path.Combine(parent, "." + Path.GetFileName(targetDir) + ".tmp-" + tag);
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var f in m.Files)
            {
                var dest = Path.Combine(tmp, f.Name);
                using (var src = _open(f.Name) ?? throw new DriverPackageException(Loc.T("driverPackage.bundledDriverMissingFile", f.Name)))
                using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(fs);
                // 读回磁盘上的文件再校验（pnputil 读的就是它）
                using var check = File.OpenRead(dest);
                var (hash, len) = Hash(check);
                if (len != f.Length)
                    throw new DriverPackageException(Loc.T("driverPackage.bytesManifestRequiresBytes", f.Name, len, f.Length));
                if (!string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DriverPackageException(Loc.T("driverPackage.sha256DoesnTMatch2", f.Name));
            }
            if (Directory.Exists(targetDir))
            {
                var old = Path.Combine(parent, "." + Path.GetFileName(targetDir) + ".old-" + tag);
                Directory.Move(targetDir, old);
                try { Directory.Move(tmp, targetDir); }
                catch { try { Directory.Move(old, targetDir); } catch { } throw; }
                TryDelete(old);
            }
            else Directory.Move(tmp, targetDir);
            return m.Files.Count;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    internal static (string Hash, long Length) Hash(Stream s)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { h.AppendData(buf, 0, n); total += n; }
        return (Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant(), total);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

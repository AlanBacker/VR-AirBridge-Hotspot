using System.Text;
using System.Text.Json;
using VRAirBridge.Core.Logging;

namespace VRAirBridge.Core.Dhcp;

/// <summary>一条 DHCP 租约（按客户端 MAC 记录）。时间均为 UTC。</summary>
public sealed class DhcpLease
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>选项 61（客户端标识）的十六进制，可为空</summary>
    public string? ClientId { get; set; }
    /// <summary>选项 12（主机名）</summary>
    public string? HostName { get; set; }
    /// <summary>选项 60（厂商类别），例如 android-dhcp-14</summary>
    public string? VendorClass { get; set; }
    public DateTime Expires { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Released { get; set; }

    public bool IsActive(DateTime nowUtc) => !Released && Expires > nowUtc;

    public DhcpLease Copy() => (DhcpLease)MemberwiseClone();
}

/// <summary>
/// 持久化的租约表（dhcp-leases.json）。线程安全；保存采用“先写临时文件再替换”的方式，断电也不会写坏。
/// path 为 null 时只存在内存里（单元测试用）。
/// </summary>
public sealed class DhcpLeaseStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, DhcpLease> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;

    public DhcpLeaseStore(string? path)
    {
        _path = path;
        Load();
    }

    public string? Path => _path;

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<DhcpLease>>(File.ReadAllText(_path, Encoding.UTF8), Json) ?? new();
            lock (_lock)
            {
                foreach (var l in list)
                    if (!string.IsNullOrWhiteSpace(l.Mac) && !string.IsNullOrWhiteSpace(l.Ip)) _byMac[l.Mac] = l;
            }
            Logger.Debug(Loc.T("dhcpLease.loadedDhcpLeaseRecords", list.Count));
        }
        catch (Exception ex)
        {
            Logger.Warn(Loc.T("dhcpLease.dhcpLeaseFileDamagedWas", ex.Message));
        }
    }

    public void Save()
    {
        if (_path == null) return;
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_byMac.Values.OrderBy(l => l.Ip, StringComparer.Ordinal).ToList(), Json);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            Logger.Debug(Loc.T("dhcpLease.failedSaveDhcpLeases") + ex.Message);
        }
    }

    public DhcpLease? GetByMac(string mac)
    {
        lock (_lock) return _byMac.TryGetValue(mac, out var l) ? l.Copy() : null;
    }

    public DhcpLease? GetByIp(string ip)
    {
        lock (_lock) return _byMac.Values.FirstOrDefault(l => l.Ip == ip)?.Copy();
    }

    public List<DhcpLease> All()
    {
        lock (_lock) return _byMac.Values.Select(l => l.Copy()).ToList();
    }

    public void Upsert(DhcpLease lease)
    {
        lock (_lock)
        {
            // 同一个 IP 只能属于一个 MAC：把其它 MAC 上的同 IP 旧记录删掉
            foreach (var other in _byMac.Values.Where(l => l.Ip == lease.Ip && !string.Equals(l.Mac, lease.Mac, StringComparison.OrdinalIgnoreCase)).ToList())
                _byMac.Remove(other.Mac);
            _byMac[lease.Mac] = lease.Copy();
        }
    }

    public bool Remove(string mac)
    {
        lock (_lock) return _byMac.Remove(mac);
    }

    /// <summary>删除过期很久（默认 7 天）的记录，避免文件无限增长。</summary>
    public int Prune(DateTime nowUtc, TimeSpan keep)
    {
        lock (_lock)
        {
            var old = _byMac.Values.Where(l => (l.Released || l.Expires < nowUtc) && l.LastSeen < nowUtc - keep).Select(l => l.Mac).ToList();
            foreach (var m in old) _byMac.Remove(m);
            return old.Count;
        }
    }
}

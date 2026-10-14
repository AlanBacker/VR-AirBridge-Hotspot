using VRAirBridge.Core.Beacon;
using VRAirBridge.Core.Clients;
using VRAirBridge.Core.Dhcp;
using VRAirBridge.Core.Hotspot;
using VRAirBridge.Core.Nat;
using VRAirBridge.Core.Net;
using VRAirBridge.Core.SystemSetup;

namespace VRAirBridge.Core;

public enum OverallState { Stopped, Starting, Running, Retrying, Error }

/// <summary>已连接/最近连接过的设备。</summary>
public sealed record ClientRow(string Mac, string? Ip, string? HostName, DateTime? LastSeen, bool Online, string? Vendor);

public enum NotifyKind { Info, Warning, Error }

public sealed record Notification(NotifyKind Kind, string Title, string Text);

/// <summary>界面显示用的完整状态快照（看门狗每个周期生成一次）。</summary>
public sealed class AppStatus
{
    public DateTime Time { get; init; } = DateTime.Now;
    public OverallState State { get; init; }
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? BusyText { get; init; }
    public bool DesiredOn { get; init; }
    public bool IsAdmin { get; init; }
    public HotspotSnapshot? Hotspot { get; init; }
    public DongleInfo? Dongle { get; init; }
    public GoAdapterInfo? Go { get; init; }
    public bool? GoOnDongle { get; init; }
    public BeaconResult? Beacon { get; init; }
    public string? BeaconNote { get; init; }
    public bool BeaconRunning { get; init; }
    public DhcpStatus Dhcp { get; init; } = new(DhcpProvider.None, Loc.T("status.notRunning"), null);
    public NatStatus Nat { get; init; } = new(NatProvider.None, Loc.T("status.notRunning"), false);
    public string? UplinkName { get; init; }
    public IReadOnlyList<ClientRow> Clients { get; init; } = Array.Empty<ClientRow>();
    /// <summary>Join monitor: every device seen joining (stage, last failure), most recent first. Includes devices that never got connected.</summary>
    public IReadOnlyList<ClientJoinRecord> Joins { get; init; } = Array.Empty<ClientJoinRecord>();
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();
    /// <summary>D-Link 收信号比内置网卡弱很多（多半是 USB 口位置不好）：界面上显示换口示意图。</summary>
    public bool RxWeak { get; init; }
    public int? RetryInSeconds { get; init; }
    /// <summary>D-Link 驱动状态（每个周期用已查到的信息快速判断）。</summary>
    public DriverState Driver { get; init; } = new(DriverStateKind.DongleNotPresent);
    /// <summary>最近一次信道扫描 / 信标核对的结果。</summary>
    public ChannelReport? Channel { get; init; }
    /// <summary>实验功能“钉住信道”的状态。</summary>
    public PinStatus? Pin { get; init; }
    /// <summary>internet.allow：是否允许连接的设备经由电脑上网。</summary>
    public bool InternetAllowed { get; init; } = true;

    public static string HeadlineFor(OverallState s) => s switch
    {
        OverallState.Running => Loc.T("status.hotspotRunning"),
        OverallState.Starting => Loc.T("status.starting"),
        OverallState.Retrying => Loc.T("status.retrying"),
        OverallState.Error => Loc.T("status.error"),
        _ => Loc.T("status.stopped"),
    };
}

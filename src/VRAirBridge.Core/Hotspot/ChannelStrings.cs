namespace VRAirBridge.Core.Hotspot;

/// <summary>“信道”面板和实验功能“钉住信道”的所有用户可见文字（从字符串表读取，随界面语言切换）。</summary>
public static class ChannelStrings
{
    public static string GroupTitle => Loc.T("channel.groupTitle");
    public static string CurrentLabel => Loc.T("channel.currentLabel");
    public static string NeighborsLabel => Loc.T("channel.neighborsLabel");
    public static string BlocksLabel => Loc.T("channel.blocksLabel");
    public static string PinLabel => Loc.T("channel.pinLabel");
    public static string PinAuto => Loc.T("channel.pinAuto");
    public static string PinWarning => Loc.T("channel.pinWarning");
    public static string ScanButton => Loc.T("channel.scanButton");
    public static string ScanTooltip => Loc.T("channel.scanTooltip");
    public static string Scanning => Loc.T("channel.scanning");

    public static string GoUnknown => Loc.T("channel.goUnknown");
    public static string GoOff => Loc.T("channel.goOff");
    public static string NoScan => Loc.T("channel.noScan");
    public static string NoNeighbors => Loc.T("channel.noNeighbors");
    public static string DfsBlock => Loc.T("channel.dfsBlock");
    public static string BlockFree => Loc.T("channel.blockFree");
    public static string NoScanner => Loc.T("channel.noScanner");
    public static string ScanFailedPrefix => Loc.T("channel.scanFailedPrefix");
    public static string DongleOnlyScanNote => Loc.T("channel.dongleOnlyScanNote");

    public static string CandidateUplink => Loc.T("channel.candidateUplink");
    public static string CandidateDfs => Loc.T("channel.candidateDfs");
    public static string Candidate24 => Loc.T("channel.candidate24");
    public static string CandidateNoProfile => Loc.T("channel.candidateNoProfile");

    // 钉住过程
    public static string PinFallback => Loc.T("channel.pinFallback");
    public static string PinNeedAdmin => Loc.T("channel.pinNeedAdmin");
    public static string PinNoProfile => Loc.T("channel.pinNoProfile");
    public static string PinConnectFailed => Loc.T("channel.pinConnectFailed");
    public static string PinDfs => Loc.T("channel.pinDfs");
    public static string Pin24 => Loc.T("channel.pin24");
    public static string PinConnecting => Loc.T("channel.pinConnecting");
    public static string PinActive => Loc.T("channel.pinActive");
    public static string PinVerified => Loc.T("channel.pinVerified");
    public static string PinUnverified => Loc.T("channel.pinUnverified");
    public static string PinOff => Loc.T("channel.pinOff");
    public static string PinSuppressed => Loc.T("channel.pinSuppressed");
    public static string NotifyTitle => Loc.T("channel.notifyTitle");
    public static string PinSavedOff => Loc.T("channel.pinSavedOff");
    public static string PinReleasedNow => Loc.T("channel.pinReleasedNow");
    public static string PinNextStart => Loc.T("channel.pinNextStart");
    public static string PinSavedNow => Loc.T("channel.pinSavedNow");
    public static string PinPickDfs => Loc.T("channel.pinPickDfs");
    public static string PinPick24 => Loc.T("channel.pinPick24");
    public static string PinPickNoProfile => Loc.T("channel.pinPickNoProfile");
    public static string PinWhat => Loc.T("channel.pinWhat");
    public static string PinFallbackLog => Loc.T("channel.pinFallbackLog");
}

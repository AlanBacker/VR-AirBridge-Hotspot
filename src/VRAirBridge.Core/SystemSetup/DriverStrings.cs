namespace VRAirBridge.Core.SystemSetup;

/// <summary>“内置驱动安装”功能的所有用户可见文字（从字符串表读取，随界面语言切换）。</summary>
public static class DriverStrings
{
    public const string BundledVersion = "1.27.831.2023";
    public const string BundledDate = "2023-08-31";

    /// <summary>D-Link 插着但没有驱动时的提示（不再推荐任何第三方软件）。</summary>
    public static string NoDriverHint => Loc.T("driver.noDriverHint");

    public static string ButtonInstall => Loc.T("driver.buttonInstall");
    public static string ButtonReinstall => Loc.T("driver.buttonReinstall");
    public static string ButtonTooltip => Loc.T("driver.buttonTooltip");

    public static string KindNotPresent => Loc.T("driver.kindNotPresent");
    public static string KindNotPresentStaged => Loc.T("driver.kindNotPresentStaged");
    public static string KindPresentNoDriver => Loc.T("driver.kindPresentNoDriver");
    public static string KindOk => Loc.T("driver.kindOk");
    public static string KindOther => Loc.T("driver.kindOther");

    public static string PromptTitle => Loc.T("driver.promptTitle");
    public static string PromptHeading => Loc.T("driver.promptHeading");
    public static string PromptText => Loc.T("driver.promptText");
    public static string PromptInstallNow => Loc.T("driver.promptInstallNow");
    public static string PromptLater => Loc.T("driver.promptLater");
    public static string InlineWarning => Loc.T("driver.inlineWarning", NoDriverHint);
    public static string StartNeedsDriver => Loc.T("driver.startNeedsDriver", NoDriverHint);
    public static string NotifyTitle => Loc.T("driver.notifyTitle");
    public static string NotifyText => Loc.T("driver.notifyText");

    public static string ReinstallConfirm => Loc.T("driver.reinstallConfirm");
    public static string OtherVersionConfirm => Loc.T("driver.otherVersionConfirm");
    public static string NeedAdmin => Loc.T("driver.needAdmin");
    public static string CliNeedAdmin => Loc.T("driver.cliNeedAdmin");
    public static string Busy => Loc.T("driver.busy");
    public static string ResultTitle => Loc.T("driver.resultTitle");
    public static string ResultFooter => Loc.T("driver.resultFooter");

    public static string Installed => Loc.T("driver.installed");
    public static string Staged259 => Loc.T("driver.staged259");
    public static string Staged259Present => Loc.T("driver.staged259Present");
    public static string Reboot3010 => Loc.T("driver.reboot3010");
    public static string FailedPrefix => Loc.T("driver.failedPrefix");
    public static string NoAdapterYet => Loc.T("driver.noAdapterYet");
    public static string VerifyFailed => Loc.T("driver.verifyFailed");
}

using System.Collections.Immutable;

namespace XiaoK.Core;

/// <summary>Only an explicit, deterministic user request can select an application UI snapshot.</summary>
public static class ApplicationUiAutomationPolicy
{
    public const string ToolId = "app.ui.snapshot.v1";
    public const string ScopedSnapshotToolId = "app.ui.snapshot-scoped.v1";
    public const string WaitForEnabledToolId = "app.ui.wait-enabled.v1";
    public const string SetTextToolId = "app.ui.set-text.v1";
    public const string InvokeButtonToolId = "app.ui.invoke-button.v1";
    private static readonly string[] Prefixes = ["读取界面控件：", "检查界面控件：", "查看界面控件："];
    private static readonly string[] GenericButtonBlockedMarkers =
    [
        "outlook", "thunderbird", "mailbird", "windowscommunicationsapps", "teams", "slack", "telegram",
        "whatsapp", "signal", "discord", "skype", "dingtalk", "lark", "feishu", "wecom", "wxwork",
        "enterprisewechat", "enterprise wechat", "wechatwork", "wechat work", "企业微信", "viber", "messenger",
        "chrome", "firefox", "brave", "chromium", "opera", "vivaldi", "browser", "filemanager", "file-manager",
        "fileexplorer", "file explorer", "directory opus", "totalcmd", "dopus", "settings", "systemsettings",
        "controlpanel", "control panel", "control.exe", "regedit", "registry", "taskmgr", "task manager",
        "windows security", "credential manager", "管理员", "管理权限", "控制面板", "注册表", "任务管理器",
        "Windows安全中心", "凭据管理器", "邮件", "邮箱", "钉钉", "飞书", "即时通讯", "浏览器", "文件管理器"
    ];
    private static readonly string[] SensitiveButtonAutomationIdMarkers =
    [
        "send", "delete", "remove", "trash", "destroy", "pay", "payment", "purchase", "checkout", "transfer",
        "withdraw", "approve", "confirm", "submit", "permission", "grant", "revoke", "admin", "elevate",
        "install", "uninstall", "reset", "security", "credential", "password", "token", "secret", "wallet",
        "发送", "删除", "移除", "回收", "支付", "付款", "购买", "结账", "转账", "提现", "审批", "确认",
        "提交", "权限", "授权", "撤销", "管理员", "安装", "卸载", "重置", "安全", "凭据", "密码", "密钥", "钱包"
    ];
    private const string ScopedSnapshotPrefix = "检查界面区域：";
    private static readonly ImmutableHashSet<string> ScopedRootControlTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Pane", "Group", "Tab", "List", "Tree", "DataGrid", "Table");
    private const string WaitForEnabledPrefix = "等待界面控件可用：";
    private const string SetTextPrefix = "设置界面文本：";
    private const string InvokeButtonPrefix = "点击界面按钮：";

    public static bool IsUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && (Prefixes.Any(prefix => request.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || IsScopedSnapshotUserRequest(request));

    public static bool IsScopedSnapshotUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && request.TrimStart().StartsWith(ScopedSnapshotPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedScopedRootControlType(string? controlType) =>
        controlType is not null && ScopedRootControlTypes.Contains(controlType);

    public static bool IsWaitForEnabledUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && request.TrimStart().StartsWith(WaitForEnabledPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSetTextUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && request.TrimStart().StartsWith(SetTextPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsInvokeButtonUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && request.TrimStart().StartsWith(InvokeButtonPrefix, StringComparison.OrdinalIgnoreCase);

    public static ToolProposal? CreateSetTextToolProposal(string request,
        IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;
        var appAliases = configuredApps.ToArray();
        var command = request.TrimStart();
        if (!command.StartsWith(SetTextPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var target = command[SetTextPrefix.Length..];
        var first = target.IndexOf('|');
        var second = first < 0 ? -1 : target.IndexOf('|', first + 1);
        if (first <= 0 || second <= first + 1) return null;

        var appName = target[..first].Trim();
        var automationId = target[(first + 1)..second].Trim();
        var value = target[(second + 1)..];
        if (appName.Length is 0 or > 96 || automationId.Length is 0 or > 96
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || !IsAllowedText(value)) return null;

        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, appAliases);
        var displayName = intent is null ? null : appAliases.FirstOrDefault(alias =>
            string.Equals(alias.AppId, intent.AppId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        if (intent is null || IsTextEntryTargetBlocked(intent.AppId, displayName, null)) return null;
        if (IsSensitiveTextEntryField(automationId)) return null;

        return new ToolProposal(SetTextToolId,
            ImmutableDictionary<string, string>.Empty
                .Add("app_id", intent.AppId)
                .Add("automation_id", automationId)
                .Add("value", value), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiValueSet);
    }

    public static bool IsAllowedText(string? value) => !string.IsNullOrEmpty(value)
        && value.Length <= 2_000
        && value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    public static bool IsMessagingClient(string? applicationId) => applicationId is not null
        && (applicationId.Equals("wechat", StringComparison.OrdinalIgnoreCase)
            || applicationId.Equals("qq", StringComparison.OrdinalIgnoreCase));

    public static bool IsMessagingClientTarget(string? applicationId, string? displayName, string? executablePath)
    {
        if (IsMessagingClient(applicationId)) return true;
        var normalizedName = displayName?.Trim();
        if (normalizedName is not null && (normalizedName.Equals("微信", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("WeChat", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("腾讯QQ", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("企业微信", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("WeCom", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Equals("WeChat Work", StringComparison.OrdinalIgnoreCase))) return true;
        var executableName = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileName(executablePath);
        return executableName.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("QQ.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("WXWork.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("WXWorkLocal.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTextEntryBlockedApplication(string? applicationId) => IsMessagingClient(applicationId)
        || applicationId is not null && (applicationId.Equals("edge", StringComparison.OrdinalIgnoreCase)
            || applicationId.Equals("explorer", StringComparison.OrdinalIgnoreCase));

    public static bool IsSensitiveTextEntryTarget(string? applicationId, string? displayName, string? executablePath)
    {
        string[] markers =
        [
            "wallet", "bank", "banking", "payment", "paypal", "stripe", "trading", "exchange",
            "crypto", "cryptocurrency", "password", "credential", "authenticator", "passkey", "secret-manager",
            "钱包", "支付", "付款", "交易", "银行", "网银", "验证码", "口令", "密码", "密钥", "秘钥",
            "助记词", "数字资产", "加密货币"
        ];
        var executableName = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileName(executablePath);
        return markers.Any(marker => (applicationId?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
            || (displayName?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
            || executableName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTextEntryTargetBlocked(string? applicationId, string? displayName, string? executablePath)
    {
        if (IsTextEntryBlockedApplication(applicationId)
            || IsSensitiveTextEntryTarget(applicationId, displayName, executablePath)) return true;
        var executableName = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileName(executablePath);
        return executableName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("QQ.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGenericButtonInvocationTargetBlocked(string? applicationId, string? displayName,
        string? executablePath)
    {
        if (IsTextEntryTargetBlocked(applicationId, displayName, executablePath)) return true;
        var executableName = string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileName(executablePath);
        if (GenericButtonBlockedMarkers.Any(marker =>
            (applicationId?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
            || (displayName?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
            || executableName.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;
        return IsExactKnownCommunicationClient(applicationId) || IsExactKnownCommunicationClient(displayName)
            || executableName.Equals("line.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("mail.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("files.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGenericButtonInvocationSelectorBlocked(string? automationId) =>
        string.IsNullOrWhiteSpace(automationId)
        || SensitiveButtonAutomationIdMarkers.Any(marker =>
            automationId.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsExactKnownCommunicationClient(string? value) => value is not null
        && (value.Trim().Equals("line", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("mail", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("email", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("messages", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("邮件", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("邮箱", StringComparison.OrdinalIgnoreCase));

    public static bool IsSensitiveTextEntryField(string? automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId)) return true;
        string[] markers =
        [
            "password", "passwd", "pwd", "passcode", "pin", "otp", "verification", "authcode", "secret",
            "token", "apikey", "privatekey", "accesskey", "seedphrase", "mnemonic", "wallet", "payment",
            "checkout", "transfer", "trading", "bank", "cardnumber", "cvv", "cvc",
            "密码", "口令", "验证码", "校验码", "支付", "付款", "交易", "银行卡", "网银", "密钥", "秘钥",
            "助记词", "钱包", "种子短语", "授权码"
        ];
        return markers.Any(marker => automationId.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static ToolProposal? CreateUserToolProposal(string request, IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;
        var appAliases = configuredApps.ToArray();

        var trimmed = request.Trim();
        var prefix = Prefixes.FirstOrDefault(candidate => trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return null;
        var appName = trimmed[prefix.Length..].Trim(' ', '\t', '：', ':', '“', '”', '"');
        if (appName.Length is 0 or > 96 || appName.Contains('\n') || appName.Contains('\r')) return null;

        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, appAliases);
        var displayName = intent is null ? null : appAliases.FirstOrDefault(alias =>
            string.Equals(alias.AppId, intent.AppId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        if (intent is null || IsMessagingClientTarget(intent.AppId, displayName, null)) return null;

        return new ToolProposal(ToolId,
            ImmutableDictionary<string, string>.Empty.Add("app_id", intent.AppId), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiSnapshotReturned);
    }

    public static ToolProposal? CreateScopedSnapshotToolProposal(string request,
        IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;
        var appAliases = configuredApps.ToArray();

        var command = request.TrimStart();
        if (!command.StartsWith(ScopedSnapshotPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var target = command[ScopedSnapshotPrefix.Length..].Trim();
        var first = target.IndexOf('|');
        var second = first < 0 ? -1 : target.IndexOf('|', first + 1);
        if (first <= 0 || second <= first + 1 || second != target.LastIndexOf('|')) return null;

        var appName = target[..first].Trim();
        var automationId = target[(first + 1)..second].Trim();
        var controlType = target[(second + 1)..].Trim();
        if (appName.Length is 0 or > 96 || automationId.Length is 0 or > 96
            || appName.Contains('\n') || appName.Contains('\r')
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || !IsAllowedScopedRootControlType(controlType)) return null;

        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, appAliases);
        var displayName = intent is null ? null : appAliases.FirstOrDefault(alias =>
            string.Equals(alias.AppId, intent.AppId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        if (intent is null || IsMessagingClientTarget(intent.AppId, displayName, null)) return null;

        return new ToolProposal(ScopedSnapshotToolId,
            ImmutableDictionary<string, string>.Empty
                .Add("app_id", intent.AppId)
                .Add("automation_id", automationId)
                .Add("root_control_type", controlType), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiSnapshotReturned);
    }

    public static ToolProposal? CreateWaitForEnabledToolProposal(string request,
        IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;

        var trimmed = request.Trim();
        if (!trimmed.StartsWith(WaitForEnabledPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var target = trimmed[WaitForEnabledPrefix.Length..].Trim();
        var separator = target.IndexOf('|');
        if (separator <= 0 || separator != target.LastIndexOf('|')) return null;
        var appName = target[..separator].Trim();
        var automationId = target[(separator + 1)..].Trim();
        if (appName.Length is 0 or > 96 || automationId.Length is 0 or > 96
            || appName.Contains('\n') || appName.Contains('\r')
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return null;

        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, configuredApps);
        if (intent is null) return null;

        return new ToolProposal(WaitForEnabledToolId,
            ImmutableDictionary<string, string>.Empty
                .Add("app_id", intent.AppId)
                .Add("automation_id", automationId), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiConditionObserved);
    }

    public static ToolProposal? CreateInvokeButtonToolProposal(string request,
        IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;

        var trimmed = request.Trim();
        if (!trimmed.StartsWith(InvokeButtonPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var target = trimmed[InvokeButtonPrefix.Length..].Trim();
        var separator = target.IndexOf('|');
        if (separator <= 0 || separator != target.LastIndexOf('|')) return null;
        var appName = target[..separator].Trim();
        var automationId = target[(separator + 1)..].Trim();
        if (appName.Length is 0 or > 96 || automationId.Length is 0 or > 96
            || appName.Contains('\n') || appName.Contains('\r')
            || automationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return null;

        var appAliases = configuredApps.ToArray();
        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, appAliases);
        var displayName = intent is null ? null : appAliases.FirstOrDefault(alias =>
            string.Equals(alias.AppId, intent.AppId, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        if (intent is null || IsGenericButtonInvocationTargetBlocked(intent.AppId, displayName, null)
            || IsGenericButtonInvocationSelectorBlocked(automationId)) return null;

        return new ToolProposal(InvokeButtonToolId,
            ImmutableDictionary<string, string>.Empty
                .Add("app_id", intent.AppId)
                .Add("automation_id", automationId), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiActionInvoked);
    }
}

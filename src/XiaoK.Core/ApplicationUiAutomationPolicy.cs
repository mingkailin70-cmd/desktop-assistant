using System.Collections.Immutable;

namespace XiaoK.Core;

/// <summary>Only an explicit, deterministic user request can select an application UI snapshot.</summary>
public static class ApplicationUiAutomationPolicy
{
    public const string ToolId = "app.ui.snapshot.v1";
    private static readonly string[] Prefixes = ["读取界面控件：", "检查界面控件：", "查看界面控件："];

    public static bool IsUserRequest(string? request) => !string.IsNullOrWhiteSpace(request)
        && Prefixes.Any(prefix => request.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static ToolProposal? CreateUserToolProposal(string request, IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;

        var trimmed = request.Trim();
        var prefix = Prefixes.FirstOrDefault(candidate => trimmed.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return null;
        var appName = trimmed[prefix.Length..].Trim(' ', '\t', '：', ':', '“', '”', '"');
        if (appName.Length is 0 or > 96 || appName.Contains('\n') || appName.Contains('\r')) return null;

        var intent = AppLaunchIntentResolver.ResolveApplicationStatus("检查应用：" + appName, configuredApps);
        if (intent is null) return null;

        return new ToolProposal(ToolId,
            ImmutableDictionary<string, string>.Empty.Add("app_id", intent.AppId), intent.AppId,
            ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
            ToolExpectedOutcome.ApplicationUiSnapshotReturned);
    }
}

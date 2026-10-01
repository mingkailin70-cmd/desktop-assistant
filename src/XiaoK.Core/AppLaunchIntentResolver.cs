namespace XiaoK.Core;

public sealed record AppLaunchIntent(string AppId, string? WorkspaceId = null);

/// <summary>Maps explicit user phrases to fixed local app IDs; unknown app names never select a default app.</summary>
public static class AppLaunchIntentResolver
{
    private static readonly string[] WindowActivationPrefixes = ["切换到", "切换至", "切到", "聚焦", "显示"];
    private static readonly string[] LaunchPrefixes = ["启动应用", "打开", "启动"];

    public static bool IsWindowActivationRequest(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return false;
        var phrase = Normalize(request);
        return WindowActivationPrefixes.Any(prefix => phrase.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static AppLaunchIntent? Resolve(string request) => ResolveWithPrefixes(request, LaunchPrefixes);

    public static AppLaunchIntent? ResolveWindowActivation(string request) =>
        ResolveWithPrefixes(request, WindowActivationPrefixes);

    private static AppLaunchIntent? ResolveWithPrefixes(string request, IReadOnlyList<string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        var phrase = Normalize(request);
        foreach (var prefix in prefixes)
        {
            if (!phrase.StartsWith(prefix, StringComparison.Ordinal)) continue;
            phrase = phrase[prefix.Length..];
            break;
        }
        phrase = phrase.Trim(' ', '：', ':', '“', '”', '"');
        if (phrase.EndsWith("应用", StringComparison.Ordinal)) phrase = phrase[..^2];

        if (phrase.Contains("小k项目", StringComparison.Ordinal)
            || phrase is "vscode项目" or "visualstudiocode项目" or "vs代码项目")
            return new("vscode", "xiaok");
        if (phrase is "vscode" or "visualstudiocode" or "vs代码编辑器" or "代码编辑器")
            return new("vscode", "xiaok");
        if (phrase is "edge" or "microsoftedge" or "edge浏览器" or "浏览器")
            return new("edge");
        if (phrase is "资源管理器" or "文件资源管理器" or "explorer" or "文件夹")
            return new("explorer");
        if (phrase is "微信" or "微信客户端" or "wechat" or "weixin")
            return new("wechat");
        if (phrase is "qq" or "ｑｑ" or "ＱＱ" or "腾讯qq")
            return new("qq");
        return null;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant()
        .Replace(" ", "", StringComparison.Ordinal)
        .Replace("　", "", StringComparison.Ordinal);
}

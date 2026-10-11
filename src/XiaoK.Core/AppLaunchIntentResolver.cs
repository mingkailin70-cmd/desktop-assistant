namespace XiaoK.Core;

public sealed record AppLaunchIntent(string AppId, string? WorkspaceId = null);
public sealed record ConfiguredAppAlias(string AppId, string DisplayName);

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

    public static AppLaunchIntent? Resolve(string request) => Resolve(request, []);

    public static AppLaunchIntent? Resolve(string request, IEnumerable<ConfiguredAppAlias> configuredApps) =>
        ResolveWithPrefixes(request, LaunchPrefixes, configuredApps);

    public static AppLaunchIntent? ResolveWindowActivation(string request) => ResolveWindowActivation(request, []);

    public static AppLaunchIntent? ResolveWindowActivation(string request, IEnumerable<ConfiguredAppAlias> configuredApps) =>
        ResolveWithPrefixes(request, WindowActivationPrefixes, configuredApps);

    public static bool IsBuiltInAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;
        var phrase = Normalize(alias);
        if (phrase.EndsWith("应用", StringComparison.Ordinal)) phrase = phrase[..^2];
        return ResolveBuiltIn(phrase) is not null;
    }

    private static AppLaunchIntent? ResolveWithPrefixes(string request, IReadOnlyList<string> prefixes,
        IEnumerable<ConfiguredAppAlias> configuredApps)
    {
        ArgumentNullException.ThrowIfNull(configuredApps);
        if (string.IsNullOrWhiteSpace(request)) return null;
        var phrase = Normalize(request);
        var matchedPrefix = false;
        foreach (var prefix in prefixes)
        {
            if (!phrase.StartsWith(prefix, StringComparison.Ordinal)) continue;
            phrase = phrase[prefix.Length..];
            matchedPrefix = true;
            break;
        }
        if (!matchedPrefix) return null;
        phrase = phrase.Trim(' ', '：', ':', '“', '”', '"');
        if (phrase.Length == 0) return null;

        var phraseWithoutSuffix = phrase.EndsWith("应用", StringComparison.Ordinal) ? phrase[..^2] : phrase;
        var builtIn = ResolveBuiltIn(phraseWithoutSuffix) ?? ResolveBuiltIn(phrase);
        if (builtIn is not null) return builtIn;

        var matches = FindConfiguredMatches(configuredApps, phrase);
        if (matches.Length == 0 && phraseWithoutSuffix != phrase)
            matches = FindConfiguredMatches(configuredApps, phraseWithoutSuffix);
        return matches.Length == 1 ? new AppLaunchIntent(matches[0]) : null;
    }

    private static string[] FindConfiguredMatches(IEnumerable<ConfiguredAppAlias> configuredApps, string phrase) => configuredApps
            .Where(app => app is not null && !string.IsNullOrWhiteSpace(app.AppId)
                && CustomApplicationPolicy.IsValidId(app.AppId)
                && !string.IsNullOrWhiteSpace(app.DisplayName)
                && CustomApplicationPolicy.IsValidDisplayName(app.DisplayName)
                && Normalize(app.DisplayName) == phrase)
            .Select(app => app.AppId)
            .Take(2)
            .ToArray();

    private static AppLaunchIntent? ResolveBuiltIn(string phrase)
    {
        if (phrase is "小k项目" or "小k代码项目" or "vscode项目" or "visualstudiocode项目" or "vs代码项目")
            return new("vscode", "xiaok");
        if (phrase is "vscode" or "visualstudiocode" or "vs代码编辑器" or "代码编辑器")
            return new("vscode", "xiaok");
        if (phrase is "edge" or "microsoftedge" or "edge浏览器" or "浏览器") return new("edge");
        if (phrase is "资源管理器" or "文件资源管理器" or "explorer" or "文件夹") return new("explorer");
        if (phrase is "微信" or "微信客户端" or "微信电脑版" or "wechat" or "weixin") return new("wechat");
        if (phrase is "qq" or "ｑｑ" or "ＱＱ" or "腾讯qq") return new("qq");
        return null;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant()
        .Replace(" ", "", StringComparison.Ordinal)
        .Replace("　", "", StringComparison.Ordinal);
}

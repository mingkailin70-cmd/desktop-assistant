namespace XiaoK.Core;

/// <summary>Validates user-registered desktop application names and executable paths.</summary>
public static class CustomApplicationPolicy
{
    public const int MaximumApplications = 20;
    public const int MaximumDisplayNameLength = 48;

    private static readonly string[] ActionPrefixes =
    [
        "打开", "启动", "启动应用", "切换到", "切换至", "切到", "聚焦", "显示"
    ];

    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "wsl.exe",
        "python.exe", "pythonw.exe", "node.exe", "cscript.exe", "wscript.exe", "mshta.exe"
    };

    public static string ValidateDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("自定义应用名称不能为空。", nameof(displayName));

        var normalized = displayName.Trim();
        if (normalized.Length > MaximumDisplayNameLength || normalized.Any(char.IsControl))
            throw new ArgumentException($"应用名称最多 {MaximumDisplayNameLength} 个字符，且不能包含控制字符。", nameof(displayName));

        var key = Normalize(normalized);
        if (key.Length == 0 || ActionPrefixes.Any(prefix => key.StartsWith(Normalize(prefix), StringComparison.Ordinal)))
            throw new ArgumentException("应用名称不能是小K的操作前缀；请填写应用本身的名称。", nameof(displayName));
        if (AppLaunchIntentResolver.IsBuiltInAlias(normalized))
            throw new ArgumentException("该名称已被 VS Code、Edge、资源管理器、微信或 QQ 的固定别名占用。", nameof(displayName));

        return normalized;
    }

    public static bool IsValidDisplayName(string? displayName)
    {
        try { _ = ValidateDisplayName(displayName); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string CreateId() => $"custom-{Guid.NewGuid():N}";

    public static bool IsValidId(string? id) => id is { Length: 39 }
        && id.StartsWith("custom-", StringComparison.Ordinal)
        && Guid.TryParseExact(id[7..], "N", out _);

    public static string ValidateExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("请选择本机的 .exe 程序文件。", nameof(path));

        string fullPath;
        try { fullPath = Path.GetFullPath(path.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("程序路径无效。", nameof(path), exception); }

        if (!LocalSearchRootPolicy.IsLocalDrivePath(fullPath))
            throw new ArgumentException("自定义应用只能从本机磁盘启动，不接受 UNC 或映射网络盘。", nameof(path));
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("自定义应用只接受 .exe 文件；不会运行脚本、批处理或命令行文本。", nameof(path));
        if (ScriptHosts.Contains(Path.GetFileName(fullPath)))
            throw new ArgumentException("命令解释器和脚本运行时不能加入应用白名单。", nameof(path));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到所选的程序文件。", fullPath);

        return fullPath;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant()
        .Replace(" ", "", StringComparison.Ordinal)
        .Replace("　", "", StringComparison.Ordinal);
}

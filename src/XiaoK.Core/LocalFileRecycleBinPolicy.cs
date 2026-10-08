namespace XiaoK.Core;

/// <summary>固定“移入回收站”命令的本机路径语法；不接受目录、通配符或网络路径。</summary>
public static class LocalFileRecycleBinPolicy
{
    public const int MaximumPathLength = 1_024;

    public static bool IsValidSourcePath(string? path)
    {
        if (!LocalFileCopyPolicy.IsValidSourcePath(path) || path is null
            || path.Any(char.IsControl) || path.IndexOfAny(['*', '?', '<', '>', '"', '|']) >= 0)
            return false;

        try { return !Directory.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException)
        { return false; }
    }

    public static bool TryParseRequest(string? request, out string sourcePath)
    {
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(request) || request.Length > MaximumPathLength + 64
            || !request.StartsWith("移入回收站", StringComparison.OrdinalIgnoreCase)) return false;

        var payload = request["移入回收站".Length..].TrimStart(' ', '：', ':');
        var candidate = payload.Trim();
        if (candidate.Length >= 2 && ((candidate[0] == '"' && candidate[^1] == '"')
            || (candidate[0] == '\'' && candidate[^1] == '\'')))
            candidate = candidate[1..^1];

        if (!IsValidSourcePath(candidate)) return false;
        sourcePath = candidate;
        return true;
    }
}

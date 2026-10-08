namespace XiaoK.Core;

/// <summary>限制单个本机文件移动命令的词法范围与固定语法。</summary>
public static class LocalFileMovePolicy
{
    public const int MaximumPathLength = 1_024;

    public static bool IsValidSourcePath(string? path) => LocalFileCopyPolicy.IsValidSourcePath(path);

    public static bool IsValidDestinationDirectoryPath(string? path) => IsValidLocalDirectoryPath(path);

    public static bool TryParseRequest(string? request, out string sourcePath, out string destinationDirectory)
    {
        sourcePath = "";
        destinationDirectory = "";
        if (string.IsNullOrWhiteSpace(request) || request.Length > MaximumPathLength * 2 + 64) return false;

        const string prefix = "移动文件";
        if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var payload = request[prefix.Length..].TrimStart(' ', '：', ':');
        var separator = payload.LastIndexOf(" 到 ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 3 >= payload.Length) return false;

        sourcePath = TrimOptionalQuotes(payload[..separator]);
        destinationDirectory = TrimOptionalQuotes(payload[(separator + 3)..]);
        return IsValidSourcePath(sourcePath) && IsValidDestinationDirectoryPath(destinationDirectory);
    }

    private static bool IsValidLocalDirectoryPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.Length <= MaximumPathLength
                && Path.IsPathFullyQualified(path)
                && !path.StartsWith("\\\\", StringComparison.Ordinal)
                && !path.StartsWith("//", StringComparison.Ordinal)
                && path.IndexOf(':', 2) < 0
                && path.IndexOf('\0') < 0
                && LocalSearchRootPolicy.IsLocalDrivePath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string TrimOptionalQuotes(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[^1] == '"')
            || (trimmed[0] == '\'' && trimmed[^1] == '\'')) ? trimmed[1..^1] : trimmed;
    }
}

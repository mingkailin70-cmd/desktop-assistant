namespace XiaoK.Core;

/// <summary>目录压缩只读取配置搜索根中的普通本机文件，并限制扫描深度、项目数和总大小。</summary>
public static class LocalDirectoryArchivePolicy
{
    public const int MaximumDirectoryPathLength = 1_024;
    public const int MaximumDepth = 5;
    public const int MaximumScannedEntries = 5_000;
    public const int MaximumFiles = 1_000;
    public const long MaximumFileBytes = 100L * 1024 * 1024;
    public const long MaximumTotalBytes = 500L * 1024 * 1024;
    public const long MaximumArchiveBytes = 540L * 1024 * 1024;

    public static bool IsValidSourcePath(string? path)
    {
        if (!LocalFileClassificationPolicy.IsValidDirectoryPath(path)
            || path!.Length > MaximumDirectoryPathLength) return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return !string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsSafeEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumDirectoryPathLength
            || name[0] == '/' || name.Contains('\\')
            || name.Contains(':') || name.Any(character => char.IsControl(character)
                || character is '<' or '>' or '"' or '|' or '?' or '*')) return false;
        var segments = name.Split('/');
        return segments.Length > 0 && segments.All(IsSafeSegment);
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".."
            || segment.EndsWith(' ') || segment.EndsWith('.')) return false;
        var stem = segment.Split('.')[0].TrimEnd(' ', '.');
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }
}

namespace XiaoK.Core;

public sealed record LocalSearchRoot(string Id, string Path);

/// <summary>Validates user-selected local roots before they enter the file-search allowlist.</summary>
public static class LocalSearchRootPolicy
{
    public const int MaximumRoots = 20;

    public static bool IsLocalDrivePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal)) return false;

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is null || root.Length < 3 || root[1] != ':') return false;
            return IsLocalDriveType(new DriveInfo(root).DriveType);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    public static bool IsLocalDriveType(DriveType driveType) =>
        driveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;

    public static IReadOnlyList<LocalSearchRoot> Parse(string? multilinePaths)
    {
        var entries = (multilinePaths ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            throw new ArgumentException("至少填写一个文件搜索目录。", nameof(multilinePaths));
        if (entries.Length > MaximumRoots)
            throw new ArgumentException($"文件搜索目录最多 {MaximumRoots} 个。", nameof(multilinePaths));

        var roots = new List<LocalSearchRoot>(entries.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsLocalDrivePath(entry))
                throw new ArgumentException("文件搜索目录必须位于本机固定盘、可移动盘或 RAM 盘，不能使用相对路径或网络共享（包括映射网络盘）。", nameof(multilinePaths));

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            var pathRoot = Path.GetPathRoot(path);
            if (pathRoot is null || string.Equals(path, pathRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("文件搜索目录不能直接指向磁盘根目录。", nameof(multilinePaths));
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"文件搜索目录不存在或不可访问：{path}");

            if (seen.Add(path))
                roots.Add(new LocalSearchRoot($"search-root-{roots.Count + 1:D2}", path));
        }

        if (roots.Count == 0)
            throw new ArgumentException("至少填写一个不同的文件搜索目录。", nameof(multilinePaths));
        return roots;
    }
}

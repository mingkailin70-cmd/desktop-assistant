namespace XiaoK.Core;

/// <summary>Validates local model storage paths while allowing ignored development weights.</summary>
public static class ModelRootPathPolicy
{
    public static string Validate(string? value, string? repositoryRoot)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!LocalSearchRootPolicy.IsLocalDrivePath(trimmed))
            throw new ArgumentException("模型目录必须位于本机固定盘、可移动盘或 RAM 盘，不能使用 UNC 或映射网络盘。", nameof(value));

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        var pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot is null || string.Equals(fullPath, Path.TrimEndingDirectorySeparator(pathRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("模型目录不能直接指向磁盘根目录。", nameof(value));

        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var normalizedRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            var normalizedModels = Path.Combine(normalizedRepository, "models");
            if (IsSameOrChildPath(fullPath, normalizedRepository) && !IsSameOrChildPath(fullPath, normalizedModels))
                throw new ArgumentException("开发阶段模型可以保存在仓库的 models 目录；仓库内其他目录不能作为模型目录。", nameof(value));
        }

        return fullPath;
    }

    private static bool IsSameOrChildPath(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

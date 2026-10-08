namespace XiaoK.Core;

/// <summary>只根据文件扩展名做只读分类，不打开或解释文件内容。</summary>
public static class LocalFileClassificationPolicy
{
    public const int MaximumDirectoryPathLength = 1_024;
    public const int MaximumScannedEntries = 5_000;
    public const int MaximumDepth = 5;
    public const int MaximumExamplesPerCategory = 2;

    public static bool IsValidDirectoryPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.Length <= MaximumDirectoryPathLength
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

    public static string ClassifyExtension(string? extension)
    {
        var normalized = extension?.ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".rtf" or ".odt"
                or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx" or ".ods" or ".odp" => "文档",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "图片",
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" or ".m4v" => "视频",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => "音频",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" => "压缩包",
            ".exe" or ".msi" or ".msix" or ".appx" => "安装程序",
            ".cs" or ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj"
                or ".py" or ".ipynb" or ".js" or ".ts" or ".tsx" or ".jsx" or ".json"
                or ".xml" or ".yaml" or ".yml" or ".sql" or ".html" or ".htm" or ".css"
                or ".scss" or ".toml" or ".ini" or ".sh" or ".ps1" or ".bat" => "代码与配置",
            _ => "其他"
        };
    }
}

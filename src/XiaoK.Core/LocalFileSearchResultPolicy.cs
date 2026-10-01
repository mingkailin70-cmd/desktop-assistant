namespace XiaoK.Core;

/// <summary>Formats bounded local file-search results without implying that a capped scan was exhaustive.</summary>
public static class LocalFileSearchResultPolicy
{
    public const int MaximumResults = 10;
    public const int MaximumScannedEntries = 5_000;

    public static string CreateSummary(int resultCount, int scannedEntries, bool scanLimitReached)
    {
        if (resultCount is < 0 or > MaximumResults)
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        if (scannedEntries is < 0 or > MaximumScannedEntries)
            throw new ArgumentOutOfRangeException(nameof(scannedEntries));

        if (resultCount == 0)
        {
            return scanLimitReached
                ? $"已检查 {scannedEntries} 个目录项并达到搜索上限；当前已检查范围内没有匹配文件，仍有内容未检查。"
                : "没有找到匹配文件。";
        }

        var summary = $"找到 {resultCount} 个结果：";
        if (resultCount == MaximumResults)
            summary += Environment.NewLine + $"已达到 {MaximumResults} 条显示上限，可能还有更多结果。";
        if (scanLimitReached)
            summary += Environment.NewLine + $"已检查 {scannedEntries} 个目录项并达到搜索上限，结果可能不完整。";
        return summary;
    }

    public static string CreateResponse(IReadOnlyList<string> paths, int scannedEntries, bool scanLimitReached)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > MaximumResults)
            throw new ArgumentOutOfRangeException(nameof(paths));

        var summary = CreateSummary(paths.Count, scannedEntries, scanLimitReached);
        return paths.Count == 0
            ? summary
            : summary + Environment.NewLine + string.Join(Environment.NewLine, paths);
    }
}

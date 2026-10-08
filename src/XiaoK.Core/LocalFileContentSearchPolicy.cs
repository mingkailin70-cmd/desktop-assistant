namespace XiaoK.Core;

/// <summary>限制本机文本内容搜索的范围、资源与仅返回位置的结果格式。</summary>
public static class LocalFileContentSearchPolicy
{
    public const string ToolId = "file.search.content.v1";
    public const string UserSearchRootId = "user-files";
    public const int MaximumQueryLength = 120;
    public const int MaximumResults = 10;
    public const int MaximumLineNumbersPerFile = 5;
    public const int MaximumScannedEntries = 5_000;
    public const int MaximumDepth = 5;
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const long MaximumTotalBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> SearchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log", ".cs", ".sln", ".slnx", ".csproj",
        ".props", ".targets", ".fs", ".fsproj", ".vb", ".vbproj", ".py", ".js", ".jsx",
        ".ts", ".tsx", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".sql", ".html",
        ".htm", ".css", ".scss", ".ps1", ".bat", ".cmd", ".sh", ".properties"
    };

    public static bool IsValidQuery(string? query) =>
        !string.IsNullOrWhiteSpace(query)
        && query.Length <= MaximumQueryLength
        && !query.Any(char.IsControl);

    public static bool IsSearchableExtension(string? extension) =>
        extension is not null && SearchableExtensions.Contains(extension);

    public static bool IsUserCommand(string? request) => !string.IsNullOrWhiteSpace(request)
        && new[] { "在文件内容中搜索", "搜索文件内容", "查找文件内容", "在文件中查找" }
            .Any(prefix => request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static bool TryParseUserCommand(string? request, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrWhiteSpace(request) || !IsUserCommand(request)) return false;

        foreach (var prefix in new[] { "在文件内容中搜索", "搜索文件内容", "查找文件内容", "在文件中查找" })
        {
            if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = request[prefix.Length..].Trim();
            candidate = candidate.TrimStart('：', ':').Trim();
            candidate = candidate.Trim('“', '”', '『', '』', '「', '」', '"', '\'').Trim();
            if (!IsValidQuery(candidate)) return false;
            query = candidate;
            return true;
        }

        return false;
    }

    public static ToolProposal? CreateUserToolProposal(string? request)
    {
        if (!TryParseUserCommand(request, out var query)) return null;

        var arguments = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
            .Add("query", query)
            .Add("root_id", UserSearchRootId);
        return new ToolProposal(ToolId, arguments, UserSearchRootId,
            ToolPrecondition.ConfiguredSearchRoot,
            ToolExpectedOutcome.MatchingFileContentLocationsListed);
    }

    public static string CreateSummary(int resultCount, int scannedEntries, long bytesRead, int skippedEntries,
        bool incomplete, bool scanLimitReached, bool byteLimitReached)
    {
        if (resultCount is < 0 or > MaximumResults) throw new ArgumentOutOfRangeException(nameof(resultCount));
        if (scannedEntries is < 0 or > MaximumScannedEntries) throw new ArgumentOutOfRangeException(nameof(scannedEntries));
        if (bytesRead is < 0 or > MaximumTotalBytes) throw new ArgumentOutOfRangeException(nameof(bytesRead));
        if (skippedEntries < 0) throw new ArgumentOutOfRangeException(nameof(skippedEntries));

        var summary = $"只返回路径与行号，不返回匹配正文；已检查 {scannedEntries:N0} 个目录项、读取 {bytesRead:N0} 字节文本。";
        summary += resultCount == 0 ? "当前已检查范围内没有匹配内容。" : $"找到 {resultCount} 个文件位置。";
        if (resultCount == MaximumResults) summary += $"已达到 {MaximumResults} 个结果上限。";
        if (scanLimitReached) summary += "已达到目录项扫描上限。";
        if (byteLimitReached) summary += "已达到文本读取总量上限。";
        if (skippedEntries > 0) summary += $"另有 {skippedEntries:N0} 个文件或目录项因格式、大小、权限或并发变化而跳过。";
        if (incomplete || resultCount == MaximumResults) summary += "结果可能不完整。";
        return summary;
    }

    public static string CreateResponse(IReadOnlyList<LocalFileContentMatch> matches, int scannedEntries,
        long bytesRead, int skippedEntries, bool incomplete, bool scanLimitReached, bool byteLimitReached)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count > MaximumResults) throw new ArgumentOutOfRangeException(nameof(matches));

        var response = new System.Text.StringBuilder(CreateSummary(matches.Count, scannedEntries, bytesRead,
            skippedEntries, incomplete, scanLimitReached, byteLimitReached));
        foreach (var match in matches)
        {
            response.AppendLine().Append(match.Path).Append(": 第").Append(string.Join("、", match.LineNumbers))
                .Append("行");
            if (match.MoreLocationsOmitted) response.Append("等（行号列表已截断）");
        }
        return response.ToString();
    }
}

public sealed record LocalFileContentMatch(string Path, IReadOnlyList<int> LineNumbers, bool MoreLocationsOmitted);

using System.Globalization;
using System.Text;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools
{
    private static readonly string[] FileClassificationCategories =
        ["文档", "图片", "视频", "音频", "压缩包", "安装程序", "代码与配置", "其他"];

    public Task<ToolResult> ClassifyFilesAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
        Task.Run(() => ClassifyFiles(proposal, cancellationToken), cancellationToken);

    private ToolResult ClassifyFiles(ToolProposal proposal, CancellationToken cancellationToken)
    {
        if (!proposal.Arguments.TryGetValue("directory_path", out var requestedDirectory)
            || !LocalFileClassificationPolicy.IsValidDirectoryPath(requestedDirectory))
            return new(false, "请提供设置中搜索范围内的本机目录完整路径。", "INVALID_CLASSIFICATION_DIRECTORY");

        var safeRoots = new List<(string Path, FileIdentity Identity)>();
        foreach (var configuredRoot in _searchRoots.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenOrdinaryPath(configuredRoot, expectedDirectory: true, enumerateDirectory: false,
                    out var rootHandle, out var canonicalRoot, out var rootIdentity)) continue;
            rootHandle.Dispose();
            if (safeRoots.All(root => !root.Path.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)))
                safeRoots.Add((canonicalRoot, rootIdentity));
        }
        if (safeRoots.Count == 0)
            return new(false, "没有可用的已配置搜索目录；没有扫描文件。", "SEARCH_ROOT_UNAVAILABLE");

        if (!TryOpenOrdinaryPath(requestedDirectory, expectedDirectory: true, enumerateDirectory: false,
                out var selectedHandle, out var selectedPath, out var selectedIdentity))
            return new(false, "目标目录不存在、被占用，或是链接等特殊目录；没有扫描内容。", "CLASSIFICATION_DIRECTORY_UNAVAILABLE");
        selectedHandle.Dispose();

        var allowedRoot = safeRoots.FirstOrDefault(root => IsWithinRoot(selectedPath, root.Path));
        if (string.IsNullOrEmpty(allowedRoot.Path))
            return new(false, "目标目录不在设置中允许的搜索范围内；没有扫描内容。", "CLASSIFICATION_DIRECTORY_OUTSIDE_ROOT");

        var counts = FileClassificationCategories.ToDictionary(category => category, _ => 0,
            StringComparer.Ordinal);
        var examples = FileClassificationCategories.ToDictionary(category => category, _ => new List<string>(),
            StringComparer.Ordinal);
        var scannedEntries = 0;
        var skippedEntries = 0;
        var depthLimitReached = false;
        var queue = new Queue<(string Path, int Depth, FileIdentity? ExpectedIdentity)>();
        queue.Enqueue((selectedPath, 0, selectedIdentity));

        while (queue.Count > 0 && scannedEntries < LocalFileClassificationPolicy.MaximumScannedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth, expectedIdentity) = queue.Dequeue();
            if (!TryOpenOrdinaryPath(current, expectedDirectory: true, enumerateDirectory: true,
                    out var directoryHandle, out var canonicalDirectory, out var directoryIdentity))
            {
                skippedEntries++;
                continue;
            }

            using (directoryHandle)
            {
                if (expectedIdentity.HasValue && expectedIdentity.Value != directoryIdentity
                    || !IsWithinRoot(canonicalDirectory, allowedRoot.Path)
                    || !IsWithinRoot(canonicalDirectory, selectedPath))
                {
                    skippedEntries++;
                    continue;
                }

                var remaining = LocalFileClassificationPolicy.MaximumScannedEntries - scannedEntries;
                if (!TryEnumerateDirectoryEntries(directoryHandle, directoryIdentity, remaining, out var entries))
                {
                    skippedEntries++;
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedEntries++;
                    if ((entry.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                        || (entry.Attributes & (uint)FileAttributes.Device) != 0)
                    {
                        skippedEntries++;
                        continue;
                    }

                    var entryPath = Path.Combine(canonicalDirectory, entry.Name);
                    if ((entry.Attributes & (uint)FileAttributes.Directory) != 0)
                    {
                        if (depth >= LocalFileClassificationPolicy.MaximumDepth)
                        {
                            depthLimitReached = true;
                            continue;
                        }

                        if (!TryOpenOrdinaryPath(entryPath, expectedDirectory: true, enumerateDirectory: false,
                                out var childHandle, out var childPath, out var childIdentity))
                        {
                            skippedEntries++;
                            continue;
                        }
                        childHandle.Dispose();
                        if (childIdentity != entry.Identity || !IsWithinRoot(childPath, allowedRoot.Path)
                            || !IsWithinRoot(childPath, selectedPath))
                        {
                            skippedEntries++;
                            continue;
                        }
                        queue.Enqueue((childPath, depth + 1, childIdentity));
                        continue;
                    }

                    if (!TryOpenOrdinaryPath(entryPath, expectedDirectory: false, enumerateDirectory: false,
                            out var fileHandle, out var filePath, out var fileIdentity))
                    {
                        skippedEntries++;
                        continue;
                    }
                    fileHandle.Dispose();
                    if (fileIdentity != entry.Identity || !IsWithinRoot(filePath, allowedRoot.Path)
                        || !IsWithinRoot(filePath, selectedPath))
                    {
                        skippedEntries++;
                        continue;
                    }

                    var category = LocalFileClassificationPolicy.ClassifyExtension(Path.GetExtension(filePath));
                    counts[category]++;
                    if (examples[category].Count < LocalFileClassificationPolicy.MaximumExamplesPerCategory)
                    {
                        var relativePath = Path.GetRelativePath(ToDisplayPath(selectedPath), ToDisplayPath(filePath));
                        examples[category].Add(SanitizeExamplePath(relativePath));
                    }

                    if (scannedEntries >= LocalFileClassificationPolicy.MaximumScannedEntries) break;
                }
            }
        }

        var incomplete = scannedEntries >= LocalFileClassificationPolicy.MaximumScannedEntries
            || depthLimitReached || skippedEntries > 0 || queue.Count > 0;
        var totalFiles = counts.Values.Sum();
        var report = new StringBuilder();
        report.AppendLine($"按文件扩展名检查了 {scannedEntries:N0} 个目录项，找到 {totalFiles:N0} 个普通文件。未读取文件内容，也没有修改文件。");
        foreach (var category in FileClassificationCategories)
        {
            report.AppendLine($"{category}：{counts[category]:N0}");
            foreach (var example in examples[category]) report.AppendLine($"  · {example}");
        }
        if (incomplete)
        {
            report.Append("扫描可能不完整");
            if (scannedEntries >= LocalFileClassificationPolicy.MaximumScannedEntries)
                report.Append("：已达到 5,000 个目录项上限");
            if (depthLimitReached) report.Append("：部分子目录超过 5 层深度");
            if (skippedEntries > 0) report.Append($"：有 {skippedEntries:N0} 个目录项因重解析点、访问失败或身份变化而跳过");
            report.AppendLine("。可缩小到更具体的子目录后重新查看。");
        }

        return new(true, incomplete ? "已生成部分文件分类；请注意扫描范围限制。" : "已完成只读文件分类。",
            Data: report.ToString().TrimEnd());
    }

    private static string SanitizeExamplePath(string path)
    {
        var safe = new string(path.Select(character =>
        {
            var category = char.GetUnicodeCategory(character);
            return char.IsControl(character) || category is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? '\uFFFD' : character;
        }).ToArray());
        return safe.Length <= 240 ? safe : safe[..239] + "…";
    }
}

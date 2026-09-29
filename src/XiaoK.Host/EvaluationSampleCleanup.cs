using System.IO;

namespace XiaoK.Host;

internal sealed record EvaluationFileSnapshot(string FullPath, long Length, long LastWriteUtcTicks);

internal sealed record EvaluationCleanupPreview(string RootPath, IReadOnlyList<EvaluationFileSnapshot> Files, int IgnoredEntries)
{
    public long TotalBytes => Files.Sum(file => file.Length);
}

internal static class EvaluationSampleCleanup
{
    public static EvaluationCleanupPreview Preview(string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullRoot)) return new(fullRoot, [], 0);

        var root = new DirectoryInfo(fullRoot);
        EnsureNoReparsePointsInPath(root);

        var files = new List<EvaluationFileSnapshot>();
        var ignored = 0;
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            var attributes = entry.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || entry is not FileInfo file || file.IsReadOnly)
            {
                ignored++;
                continue;
            }

            files.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
        }

        return new(fullRoot, files.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(), ignored);
    }

    public static int DeleteIfUnchanged(EvaluationCleanupPreview approvedPreview)
    {
        var current = Preview(approvedPreview.RootPath);
        if (current.IgnoredEntries != approvedPreview.IgnoredEntries
            || current.Files.Count != approvedPreview.Files.Count
            || !current.Files.SequenceEqual(approvedPreview.Files))
            throw new InvalidOperationException("评测目录在确认期间发生变化；没有删除文件，请重新预览。");

        foreach (var file in current.Files)
        {
            var attributes = File.GetAttributes(file.FullPath);
            var info = new FileInfo(file.FullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || info.IsReadOnly
                || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
                throw new InvalidOperationException("评测文件在确认期间发生变化；没有继续清理，请重新预览。");
        }

        var deleted = 0;
        foreach (var file in current.Files)
        {
            File.Delete(file.FullPath);
            deleted++;
        }

        return deleted;
    }

    private static void EnsureNoReparsePointsInPath(DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("评测目录路径包含链接或重解析点；为避免越界，不会清理此目录。");
    }
}

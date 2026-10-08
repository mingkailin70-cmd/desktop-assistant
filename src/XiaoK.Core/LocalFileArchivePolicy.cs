namespace XiaoK.Core;

/// <summary>单文件压缩仅允许读取配置搜索根中的普通本机文件。</summary>
public static class LocalFileArchivePolicy
{
    public const long MaximumSourceBytes = 100L * 1024 * 1024;

    public static bool IsValidSourcePath(string? path) => LocalFileCopyPolicy.IsValidSourcePath(path);
}

namespace XiaoK.Core;

/// <summary>Validates fixed executable names before user-selected applications enter the local allowlist.</summary>
public static class LocalDesktopAppPathPolicy
{
    public static string ValidateExecutablePath(string? path, string expectedFileName, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{applicationName}程序路径不能为空。", nameof(path));
        if (string.IsNullOrWhiteSpace(expectedFileName) || Path.GetFileName(expectedFileName) != expectedFileName)
            throw new ArgumentException("内部程序文件名规则无效。", nameof(expectedFileName));

        var fullPath = Path.GetFullPath(path.Trim());
        if (!LocalSearchRootPolicy.IsLocalDrivePath(fullPath))
            throw new ArgumentException($"{applicationName}必须位于本机磁盘，不能从网络共享或映射网络盘启动。", nameof(path));
        if (!string.Equals(Path.GetFileName(fullPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"请选择 {expectedFileName}，以确保{applicationName}白名单指向正确的程序。", nameof(path));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"找不到{applicationName}程序：{fullPath}", fullPath);
        return fullPath;
    }
}

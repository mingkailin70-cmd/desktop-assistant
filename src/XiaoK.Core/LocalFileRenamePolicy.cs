namespace XiaoK.Core;

/// <summary>固定本机文件重命名入口的词法限制。</summary>
public static class LocalFileRenamePolicy
{
    public const int MaximumSourcePathLength = 1_024;
    public const int MaximumFileNameLength = 255;

    public static bool IsValidSourcePath(string? path) => LocalFileCopyPolicy.IsValidSourcePath(path);

    public static bool IsValidFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaximumFileNameLength
            || fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Any(char.IsControl) || fileName.EndsWith(' ') || fileName.EndsWith('.')) return false;

        // Windows also reserves these device names when an extension is present.
        var stem = fileName.Split('.')[0].TrimEnd(' ', '.');
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

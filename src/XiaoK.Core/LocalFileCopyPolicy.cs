namespace XiaoK.Core;

/// <summary>Fixed limits and lexical checks for non-destructive local file copies.</summary>
public static class LocalFileCopyPolicy
{
    public const long MaximumFileBytes = 100L * 1024 * 1024;
    public const int MaximumSourcePathLength = 1_024;

    public static bool IsValidSourcePath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && path.Length <= MaximumSourcePathLength
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
}

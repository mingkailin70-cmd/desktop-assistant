namespace XiaoK.Core;

/// <summary>公网文件下载的固定资源、文件名与执行内容限制。</summary>
public static class PublicFileDownloadPolicy
{
    public const int MaximumDownloadBytes = 50 * 1024 * 1024;
    public const int MaximumFileNameLength = 180;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".appref-ms", ".application", ".appx", ".bash", ".bat", ".chm", ".cmd", ".com", ".command",
        ".cpl", ".csh", ".desktop", ".dll", ".exe", ".gadget", ".hta", ".htm", ".html", ".jar", ".js", ".jse",
        ".ksh", ".lnk", ".msi", ".msix", ".msp", ".ocx", ".pif", ".pl", ".ps1", ".ps1xml", ".ps2", ".ps2xml",
        ".psc1", ".psc2", ".psm1", ".py", ".pyw", ".rb", ".reg", ".scf", ".scr", ".sh", ".shb", ".shs",
        ".svg", ".svgz", ".sys", ".url", ".vbe", ".vbs", ".ws", ".wsc", ".wsf", ".wsh", ".xhtml"
    };

    private static readonly HashSet<string> BlockedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.microsoft.portable-executable", "application/x-bat", "application/x-dosexec",
        "application/x-msdownload", "application/x-msi", "application/x-powershell", "application/x-sh",
        "application/x-vbs", "application/x-windows-executable", "application/x-ms-shortcut",
        "application/javascript", "image/svg+xml", "text/javascript", "text/html", "application/xhtml+xml",
        "text/x-python", "text/x-shellscript"
    };

    public static bool IsAllowedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaximumFileNameLength
            || fileName is "." or ".." || fileName.EndsWith(' ') || fileName.EndsWith('.')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Any(char.IsControl)) return false;

        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd(' ', '.');
        if (stem.Length == 0 || IsReservedDeviceName(stem)) return false;
        return !BlockedExtensions.Contains(Path.GetExtension(fileName));
    }

    public static bool IsAllowedMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return true;
        var normalized = mediaType.Split(';', 2)[0].Trim();
        return !BlockedMediaTypes.Contains(normalized);
    }

    public static bool IsAllowedContent(ReadOnlySpan<byte> content)
    {
        if (content.Length < 2) return true;
        if (content[0] == (byte)'M' && content[1] == (byte)'Z'
            || content[0] == (byte)'#' && content[1] == (byte)'!') return false;
        return content.Length < 4 || !(content[0] == 0x7F && content[1] == (byte)'E'
            && content[2] == (byte)'L' && content[3] == (byte)'F');
    }

    private static bool IsReservedDeviceName(string stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
        || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
}

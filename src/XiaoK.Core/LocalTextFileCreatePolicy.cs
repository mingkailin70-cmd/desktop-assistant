using System.Text;

namespace XiaoK.Core;

/// <summary>限制用户直接请求的新建纯文本文件。</summary>
public static class LocalTextFileCreatePolicy
{
    public const string ToolId = "file.create.text.v1";
    public const string ExportTargetId = "configured-export";
    public const int MaximumFileNameLength = 128;
    public const int MaximumContentCharacters = 16_000;
    public const int MaximumContentBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly string[] ContentSeparators = ["；内容：", ";内容：", " 内容：", " 内容:"];

    public static bool TryParseRequest(string? request, out string fileName, out string content)
    {
        fileName = string.Empty;
        content = string.Empty;
        const string prefix = "创建文本文件：";
        if (string.IsNullOrWhiteSpace(request) || !request.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var remainder = request[prefix.Length..];
        var separatorIndex = -1;
        var separatorLength = 0;
        foreach (var separator in ContentSeparators)
        {
            var candidate = remainder.IndexOf(separator, StringComparison.Ordinal);
            if (candidate >= 0 && (separatorIndex < 0 || candidate < separatorIndex))
            {
                separatorIndex = candidate;
                separatorLength = separator.Length;
            }
        }
        if (separatorIndex <= 0) return false;

        fileName = remainder[..separatorIndex].Trim();
        if (fileName.Length >= 2
            && ((fileName[0] == '"' && fileName[^1] == '"') || (fileName[0] == '“' && fileName[^1] == '”')))
            fileName = fileName[1..^1];
        content = remainder[(separatorIndex + separatorLength)..];
        if (content.StartsWith(' ')) content = content[1..];
        else if (content.StartsWith('　')) content = content[1..];
        return IsValidFileName(fileName) && IsValidContent(content);
    }

    public static bool IsValidFileName(string? fileName)
    {
        if (!LocalFileRenamePolicy.IsValidFileName(fileName)
            || fileName!.Length > MaximumFileNameLength
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return false;

        var extension = Path.GetExtension(fileName);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidContent(string? content)
    {
        if (content is null || content.Length > MaximumContentCharacters
            || content.Any(character => character == '\0'
                || (char.IsControl(character) && character is not ('\r' or '\n' or '\t')))) return false;
        try { return StrictUtf8.GetByteCount(content) <= MaximumContentBytes; }
        catch (EncoderFallbackException) { return false; }
    }
}

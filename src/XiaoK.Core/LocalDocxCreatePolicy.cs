namespace XiaoK.Core;

/// <summary>限制用户直接请求的新建纯文本 Word 文档。</summary>
public static class LocalDocxCreatePolicy
{
    public const string ToolId = "file.create.docx.v1";

    public static bool TryParseRequest(string? request, out string fileName, out string content)
    {
        fileName = string.Empty;
        content = string.Empty;
        const string prefix = "创建Word文档：";
        if (string.IsNullOrWhiteSpace(request) || !request.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var remainder = request[prefix.Length..];
        var lineBreak = remainder.IndexOf('\n');
        if (lineBreak <= 0) return false;

        fileName = remainder[..lineBreak].TrimEnd('\r').Trim();
        if (fileName.Length >= 2
            && ((fileName[0] == '"' && fileName[^1] == '"') || (fileName[0] == '“' && fileName[^1] == '”')))
            fileName = fileName[1..^1];

        var bodyLine = remainder[(lineBreak + 1)..];
        if (!bodyLine.StartsWith("正文：", StringComparison.Ordinal)) return false;
        content = bodyLine[3..];
        if (content.StartsWith("\r\n", StringComparison.Ordinal)) content = content[2..];
        else if (content.StartsWith('\n')) content = content[1..];
        return IsValidFileName(fileName) && IsValidContent(content);
    }

    public static bool IsValidFileName(string? fileName) => LocalTextFileCreatePolicy.IsValidDocxFileName(fileName);

    public static bool IsValidContent(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.Length > LocalTextFileCreatePolicy.MaximumContentCharacters
            || content.Contains('\0') || content.Contains('\t') || content.Contains('\v') || content.Contains('\f')) return false;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '\r')
            {
                if (index + 1 >= content.Length || content[index + 1] != '\n') return false;
                index++;
                continue;
            }
            if (character != '\n' && char.IsControl(character)) return false;
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetByteCount(content)
                <= LocalTextFileCreatePolicy.MaximumContentBytes;
        }
        catch (System.Text.EncoderFallbackException) { return false; }
    }
}

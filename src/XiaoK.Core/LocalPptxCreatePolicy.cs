using System.Text;

namespace XiaoK.Core;

/// <summary>Parses explicitly supplied plain-text slide content for a new PowerPoint file.</summary>
public static class LocalPptxCreatePolicy
{
    public const string ToolId = "file.create.pptx.v1";
    public const int MaximumSlides = 20;
    public const int MaximumBulletsPerSlide = 8;
    public const int MaximumFieldCharacters = 280;
    public const int MaximumContentCharacters = 16_000;
    public const int MaximumContentBytes = 64 * 1024;

    public static bool TryParseRequest(string? request, out string fileName, out string content)
    {
        fileName = string.Empty;
        content = string.Empty;
        string? prefix = new[] { "创建PowerPoint：", "创建PPTX文件：", "创建演示文稿：" }
            .FirstOrDefault(candidate => request?.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) == true);
        if (prefix is null || string.IsNullOrWhiteSpace(request)) return false;

        var lines = request[prefix.Length..].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 4 || lines.Any(line => line.Contains('\r')) || !lines[1].StartsWith("标题：", StringComparison.Ordinal)
            || lines[2] != "幻灯片：") return false;

        fileName = TrimOptionalQuotes(lines[0].Trim());
        if (!LocalTextFileCreatePolicy.IsValidPptxFileName(fileName)) return false;

        var slideLines = lines.Skip(3).ToArray();
        while (slideLines.Length > 0 && slideLines[^1].Length == 0) slideLines = slideLines[..^1];
        if (slideLines.Length is < 1 or > MaximumSlides) return false;

        var fields = new List<string>(1 + slideLines.Length * 3) { lines[1]["标题：".Length..] };
        foreach (var line in slideLines)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var slide = line.Split('|');
            if (slide.Length is < 2 or > MaximumBulletsPerSlide + 1) return false;
            fields.Add(slide[0]);
            fields.Add((slide.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            fields.AddRange(slide.Skip(1));
        }

        if (!fields.All(IsValidField)) return false;
        content = string.Join('\n', fields);
        return IsValidContent(content);
    }

    public static bool IsValidFileName(string? fileName) => LocalTextFileCreatePolicy.IsValidPptxFileName(fileName);

    public static bool IsValidContent(string? content) => TryParseContent(content, out _, out _);

    public static bool TryParseContent(string? content, out string title, out IReadOnlyList<SlideContent> slides)
    {
        title = string.Empty;
        slides = [];
        if (string.IsNullOrEmpty(content) || content.Length > MaximumContentCharacters) return false;
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(content) > MaximumContentBytes) return false;
        }
        catch (EncoderFallbackException) { return false; }

        if (content.Contains('\r')) return false;
        var fields = content.Split('\n');
        if (fields.Length < 3 || fields.Any(field => !IsValidField(field))) return false;
        var parsedSlides = new List<SlideContent>();
        var index = 1;
        while (index < fields.Length)
        {
            var slideTitle = fields[index++];
            if (index >= fields.Length) return false;
            var bullets = new List<string>();
            if (!int.TryParse(fields[index++], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var bulletCount)
                || bulletCount is < 1 or > MaximumBulletsPerSlide || index + bulletCount > fields.Length) return false;
            for (var bullet = 0; bullet < bulletCount; bullet++) bullets.Add(fields[index++]);
            parsedSlides.Add(new SlideContent(slideTitle, bullets));
            if (parsedSlides.Count > MaximumSlides) return false;
        }

        if (parsedSlides.Count == 0) return false;
        title = fields[0];
        slides = parsedSlides;
        return true;
    }

    public static bool IsValidField(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumFieldCharacters
        && !value.Contains('|') && !value.Contains('\n') && !value.Contains('\r')
        && !value.Any(char.IsControl);

    private static string TrimOptionalQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '“' && value[^1] == '”')))
            return value[1..^1];
        return value;
    }

    public sealed record SlideContent(string Title, IReadOnlyList<string> Bullets);
}

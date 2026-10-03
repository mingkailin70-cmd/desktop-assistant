namespace XiaoK.Core;

/// <summary>Accepts only the fixed, low-impact local wake phrases; no arbitrary recognized text is exposed.</summary>
public static class WakeWordPhrasePolicy
{
    private static readonly HashSet<string> AllowedPhrases = new(StringComparer.Ordinal)
    {
        "嗨小k",
        "你好小k",
        "hi小k"
    };

    public static IReadOnlyList<string> Phrases { get; } = ["嗨小K", "你好小K", "Hi 小K"];

    public static bool IsWakePhrase(string? recognizedText)
    {
        if (string.IsNullOrWhiteSpace(recognizedText)) return false;
        var normalized = new string(recognizedText
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return AllowedPhrases.Contains(normalized);
    }
}

using System.Text.Json.Serialization;

namespace XiaoK.Core;

public sealed record ContactReplyStylePreference(
    string ContactName,
    string StyleId,
    string Source,
    DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore]
    public string StyleDisplayName => ContactReplyStyleCatalog.GetDisplayName(StyleId);

    [JsonIgnore]
    public string SourceDisplayName => Source == ContactReplyStyleCatalog.UserConfirmedSource ? "用户确认" : "未知来源";
}

public sealed record ContactReplyStyleOption(string Id, string DisplayName);

/// <summary>Maps user-confirmed local contact preferences to fixed prompt instructions.</summary>
public static class ContactReplyStyleCatalog
{
    public const string DefaultStyleId = "concise";
    public const string UserConfirmedSource = "user-confirmed";

    private static readonly IReadOnlyDictionary<string, string> Instructions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["concise"] = "简洁直接，优先回答重点，避免重复。",
        ["formal"] = "礼貌正式，措辞清楚，不添加未提供的承诺。",
        ["warm"] = "自然友好，保持适当距离，不假设亲密关系。",
        ["casual"] = "自然口语化，同时保持尊重。",
        ["empathetic"] = "只回应消息中明确表达的情绪，不推测对方背景。"
    };

    public static IReadOnlyList<ContactReplyStyleOption> Options { get; } = Array.AsReadOnly(new ContactReplyStyleOption[]
    {
        new ContactReplyStyleOption("concise", "简洁"),
        new ContactReplyStyleOption("formal", "正式"),
        new ContactReplyStyleOption("warm", "友好"),
        new ContactReplyStyleOption("casual", "轻松"),
        new ContactReplyStyleOption("empathetic", "共情")
    });

    public static bool IsSupportedStyle(string? styleId) =>
        styleId is not null && Instructions.ContainsKey(styleId);

    public static string GetDisplayName(string? styleId) =>
        Options.FirstOrDefault(option => option.Id == styleId)?.DisplayName ?? "未知风格";

    public static string CreateSystemPrompt(string styleId)
    {
        if (!Instructions.TryGetValue(styleId, out var instruction))
            throw new ArgumentOutOfRangeException(nameof(styleId), "回复风格不在固定选项中。");

        return "根据用户提供的单条消息起草中文回复。不得声称已发送，不得补造事实、背景或承诺；只输出草稿正文。\n回复风格：" + instruction;
    }

    public static bool TryNormalizeContactName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 80
            && !normalized.Any(char.IsControl)
            && !normalized.Contains(':')
            && !normalized.Contains('：');
    }

    public static string? FindStyleForContact(IEnumerable<ContactReplyStylePreference>? preferences, string? contactName)
    {
        if (!TryNormalizeContactName(contactName, out var normalized) || preferences is null) return null;

        foreach (var preference in preferences)
        {
            if (preference is null
                || !string.Equals(preference.Source, UserConfirmedSource, StringComparison.Ordinal)
                || !IsSupportedStyle(preference.StyleId)
                || !TryNormalizeContactName(preference.ContactName, out var savedName)) continue;
            if (string.Equals(savedName, normalized, StringComparison.OrdinalIgnoreCase)) return preference.StyleId;
        }

        return null;
    }
}

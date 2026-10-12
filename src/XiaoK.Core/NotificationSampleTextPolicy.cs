using System.Text;

namespace XiaoK.Core;

/// <summary>Bounds and sanitizes notification text while it is enumerated for an explicitly confirmed sample.</summary>
public static class NotificationSampleTextPolicy
{
    public const int MaximumTextElements = 12;
    public const int MaximumCharacters = 20_000;

    public static (IReadOnlyList<string> Elements, bool Truncated) Project(IEnumerable<string?> textElements)
    {
        ArgumentNullException.ThrowIfNull(textElements);

        var elements = new List<string>(MaximumTextElements);
        var remainingCharacters = MaximumCharacters;
        using var enumerator = textElements.GetEnumerator();

        while (elements.Count < MaximumTextElements)
        {
            if (!enumerator.MoveNext()) return (elements.ToArray(), false);

            var (safeText, textTruncated) = KeepSafePrefix(enumerator.Current, remainingCharacters);
            elements.Add(safeText);
            remainingCharacters -= safeText.Length;
            if (textTruncated) return (elements.ToArray(), true);

            if (remainingCharacters == 0)
                return (elements.ToArray(), enumerator.MoveNext());
        }

        return (elements.ToArray(), enumerator.MoveNext());
    }

    private static (string Text, bool Truncated) KeepSafePrefix(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value)) return (string.Empty, false);

        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\r' or '\n' or '\t')) continue;
            if (builder.Length + rune.Utf16SequenceLength > maximumCharacters)
                return (builder.ToString(), true);

            builder.Append(rune.ToString());
        }

        return (builder.ToString(), false);
    }
}

namespace XiaoK.Core;

/// <summary>Validates bounded, exact Windows application identifiers used by notification allowlists.</summary>
public static class AppUserModelIdPolicy
{
    public const int MaximumLength = 128;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength || value.Any(char.IsControl))
            return false;

        var separator = value.IndexOf('!');
        if (separator < 0) return true; // Unpackaged desktop apps can use a non-package AUMID.

        return separator > 0
            && separator < value.Length - 1
            && value.IndexOf('!', separator + 1) < 0;
    }
}

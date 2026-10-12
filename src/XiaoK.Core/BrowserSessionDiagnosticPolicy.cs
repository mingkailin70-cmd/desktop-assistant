namespace XiaoK.Core;

/// <summary>
/// Allows installed-package browser diagnostic paths only when explicitly isolated,
/// hidden from the user's desktop, and running with MSIX package identity.
/// </summary>
public static class BrowserSessionDiagnosticPolicy
{
    public static bool CanRunSyntheticSmoke(IEnumerable<string> arguments, bool hasPackageIdentity)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!hasPackageIdentity) return false;

        var supplied = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return supplied.Contains("--browser-session-smoke")
            && supplied.Contains("--diagnostics-profile")
            && supplied.Contains("--background");
    }

    public static bool CanRunPublicSmoke(IEnumerable<string> arguments, bool hasPackageIdentity)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!hasPackageIdentity) return false;

        var supplied = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return supplied.Contains("--browser-public-smoke")
            && supplied.Contains("--diagnostics-profile")
            && supplied.Contains("--background");
    }
}

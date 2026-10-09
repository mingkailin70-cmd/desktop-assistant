namespace XiaoK.Core;

/// <summary>
/// Allows the installed-package synthetic browser smoke path only when it is explicitly isolated,
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
}

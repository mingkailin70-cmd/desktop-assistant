namespace XiaoK.Core;

public static class StartupWindowVisibilityPolicy
{
    public static bool ShouldStartHidden(IEnumerable<string> arguments, bool isStartupTaskActivation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return isStartupTaskActivation
            || arguments.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
    }
}

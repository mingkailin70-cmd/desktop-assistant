namespace XiaoK.Core;

public readonly record struct WindowAreaSize(
    double WidthDip,
    double HeightDip,
    double MinimumWidthDip,
    double MinimumHeightDip,
    double MaximumWidthDip,
    double MaximumHeightDip);

public static class WindowAreaSizingPolicy
{
    public static WindowAreaSize FitToWorkArea(
        double requestedWidthDip,
        double requestedHeightDip,
        double preferredMinimumWidthDip,
        double preferredMinimumHeightDip,
        int workAreaWidthPixels,
        int workAreaHeightPixels,
        uint dpi)
    {
        RequirePositiveFinite(requestedWidthDip, nameof(requestedWidthDip));
        RequirePositiveFinite(requestedHeightDip, nameof(requestedHeightDip));
        RequirePositiveFinite(preferredMinimumWidthDip, nameof(preferredMinimumWidthDip));
        RequirePositiveFinite(preferredMinimumHeightDip, nameof(preferredMinimumHeightDip));

        if (workAreaWidthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(workAreaWidthPixels));
        if (workAreaHeightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(workAreaHeightPixels));
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        var maximumWidthDip = workAreaWidthPixels * 96d / dpi;
        var maximumHeightDip = workAreaHeightPixels * 96d / dpi;
        var minimumWidthDip = Math.Min(preferredMinimumWidthDip, maximumWidthDip);
        var minimumHeightDip = Math.Min(preferredMinimumHeightDip, maximumHeightDip);

        return new WindowAreaSize(
            Math.Clamp(requestedWidthDip, minimumWidthDip, maximumWidthDip),
            Math.Clamp(requestedHeightDip, minimumHeightDip, maximumHeightDip),
            minimumWidthDip,
            minimumHeightDip,
            maximumWidthDip,
            maximumHeightDip);
    }

    private static void RequirePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

namespace XiaoK.Core;

/// <summary>桌宠按固定比例缩放；用户偏好与显示器临时限制分别处理。</summary>
public static class PetSizingPolicy
{
    public const double BaseWidthDip = 300;
    public const double BaseHeightDip = 372;
    public const double MinimumPercent = 40;
    public const double MaximumPercent = 160;
    public const double DefaultPercent = 70;

    public static bool IsValidPercent(double percent) => double.IsFinite(percent)
        && percent is >= MinimumPercent and <= MaximumPercent;

    public static double ClampPercent(double percent)
    {
        if (!double.IsFinite(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        return Math.Clamp(percent, MinimumPercent, MaximumPercent);
    }

    public static WindowAreaSize FitToWorkArea(double percent, int widthPixels, int heightPixels, uint dpi)
    {
        if (!IsValidPercent(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        if (widthPixels <= 0) throw new ArgumentOutOfRangeException(nameof(widthPixels));
        if (heightPixels <= 0) throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (dpi == 0) throw new ArgumentOutOfRangeException(nameof(dpi));

        var availableScale = Math.Min(widthPixels * 96d / dpi / BaseWidthDip,
            heightPixels * 96d / dpi / BaseHeightDip);
        var maximumScale = Math.Min(MaximumPercent / 100, availableScale);
        var minimumScale = Math.Min(MinimumPercent / 100, maximumScale);
        var scale = Math.Min(percent / 100, maximumScale);
        return new(BaseWidthDip * scale, BaseHeightDip * scale,
            BaseWidthDip * minimumScale, BaseHeightDip * minimumScale,
            BaseWidthDip * maximumScale, BaseHeightDip * maximumScale);
    }
}

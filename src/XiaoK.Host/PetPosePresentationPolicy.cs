using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XiaoK.Host;

internal readonly record struct PetPoseMotionProfile(double ScaleX, double ScaleY, double OffsetY);

internal static class PetPosePresentationPolicy
{
    public static XiaoKPetPose FromStatus(string status)
    {
        if (status.Contains("采集麦克风", StringComparison.Ordinal)
            || status.Contains("聆听", StringComparison.Ordinal)
            || status.Contains("唤醒监听已开启", StringComparison.Ordinal))
            return XiaoKPetPose.Listening;

        if (status.Contains("运行中", StringComparison.Ordinal)
            || status.Contains("执行", StringComparison.Ordinal)
            || status.Contains("播报", StringComparison.Ordinal))
            return XiaoKPetPose.Executing;

        if (status.Contains("识别中", StringComparison.Ordinal)
            || status.Contains("分析", StringComparison.Ordinal)
            || status.Contains("思考", StringComparison.Ordinal)
            || status.Contains("任务队列中", StringComparison.Ordinal))
            return XiaoKPetPose.Thinking;

        return XiaoKPetPose.Idle;
    }

    public static string GetFallbackAssetFileName(XiaoKPetPose pose) => pose switch
    {
        XiaoKPetPose.Listening => "xiaok-silver-shaded-vpet-listening-v1.png",
        XiaoKPetPose.Thinking => "xiaok-silver-shaded-vpet-thinking-v1.png",
        XiaoKPetPose.Executing => "xiaok-silver-shaded-vpet-executing-v1.png",
        _ => "xiaok-silver-shaded-3d-v15.png"
    };

    public static bool ShouldAnimate(XiaoKPetPose? previous, XiaoKPetPose current,
        bool isVisible, bool clientAreaAnimationEnabled) =>
        previous.HasValue && previous.Value != current && isVisible && clientAreaAnimationEnabled;

    public static PetPoseMotionProfile GetMotionProfile(XiaoKPetPose pose) => pose switch
    {
        XiaoKPetPose.Listening => new(1.010, 1.024, -1.5),
        XiaoKPetPose.Thinking => new(1.016, 1.012, -1.0),
        XiaoKPetPose.Executing => new(1.022, 0.996, 0.8),
        _ => new(1.016, 1.016, -1.0)
    };
}

internal static class PetPoseTransitionAnimator
{
    private static readonly Duration HalfCycle = TimeSpan.FromMilliseconds(170);

    public static void Play(ScaleTransform scale, TranslateTransform offset, PetPoseMotionProfile profile)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(1, profile.ScaleX));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(1, profile.ScaleY));
        offset.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0, profile.OffsetY));
    }

    private static DoubleAnimation CreateAnimation(double from, double to) => new(from, to, HalfCycle)
    {
        AutoReverse = true,
        FillBehavior = FillBehavior.Stop,
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
    };
}

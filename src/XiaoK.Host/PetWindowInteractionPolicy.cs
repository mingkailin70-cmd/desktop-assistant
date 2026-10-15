namespace XiaoK.Host;

internal enum PetWindowMoveCompletion
{
    Ignore,
    Expand,
    SavePosition
}

internal enum PetWindowNudgeDirection
{
    Left,
    Right,
    Up,
    Down
}

internal static class PetWindowInteractionPolicy
{
    private const int KeyboardNudgeStepDip = 80;

    public static bool ShouldStartMove(bool isLeftButton, bool isPressed, bool isOverResizeHandle, bool isInteractiveSource)
        => isLeftButton && isPressed && !isOverResizeHandle && !isInteractiveSource;

    public static bool ShouldTreatButtonAsInteractive(bool isPetBodyButton, bool isExpanded)
        => !isPetBodyButton || isExpanded;

    public static (int HorizontalDip, int VerticalDip) GetKeyboardNudgeOffsetDip(PetWindowNudgeDirection direction)
        => direction switch
        {
            PetWindowNudgeDirection.Left => (-KeyboardNudgeStepDip, 0),
            PetWindowNudgeDirection.Right => (KeyboardNudgeStepDip, 0),
            PetWindowNudgeDirection.Up => (0, -KeyboardNudgeStepDip),
            PetWindowNudgeDirection.Down => (0, KeyboardNudgeStepDip),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

    public static (int HorizontalPixels, int VerticalPixels) GetKeyboardNudgeOffsetPixels(
        PetWindowNudgeDirection direction, uint effectiveDpi)
    {
        if (effectiveDpi == 0) throw new ArgumentOutOfRangeException(nameof(effectiveDpi));
        var (horizontalDip, verticalDip) = GetKeyboardNudgeOffsetDip(direction);
        return (ScaleToPixels(horizontalDip, effectiveDpi), ScaleToPixels(verticalDip, effectiveDpi));
    }

    private static int ScaleToPixels(int dip, uint effectiveDpi) =>
        checked((int)Math.Round(dip * (double)effectiveDpi / 96d, MidpointRounding.AwayFromZero));

    public static PetWindowMoveCompletion GetCompletion(bool positionChanged, bool cancelled, bool expandOnClick)
    {
        if (positionChanged) return PetWindowMoveCompletion.SavePosition;
        return cancelled || !expandOnClick
            ? PetWindowMoveCompletion.Ignore
            : PetWindowMoveCompletion.Expand;
    }
}

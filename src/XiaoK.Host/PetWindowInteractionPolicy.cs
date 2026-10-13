namespace XiaoK.Host;

internal enum PetWindowMoveCompletion
{
    Ignore,
    Expand,
    SavePosition
}

internal static class PetWindowInteractionPolicy
{
    public static bool ShouldStartMove(bool isLeftButton, bool isPressed, bool isOverResizeHandle, bool isInteractiveSource)
        => isLeftButton && isPressed && !isOverResizeHandle && !isInteractiveSource;

    public static bool ShouldTreatButtonAsInteractive(bool isPetBodyButton, bool isExpanded)
        => !isPetBodyButton || isExpanded;

    public static PetWindowMoveCompletion GetCompletion(bool positionChanged, bool cancelled, bool expandOnClick)
    {
        if (positionChanged) return PetWindowMoveCompletion.SavePosition;
        return cancelled || !expandOnClick
            ? PetWindowMoveCompletion.Ignore
            : PetWindowMoveCompletion.Expand;
    }
}

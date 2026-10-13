namespace XiaoK.Host;

internal enum PetWindowMoveCompletion
{
    Ignore,
    Expand,
    SavePosition
}

internal static class PetWindowInteractionPolicy
{
    public static PetWindowMoveCompletion GetCompletion(bool positionChanged, bool cancelled, bool expandOnClick)
    {
        if (positionChanged) return PetWindowMoveCompletion.SavePosition;
        return cancelled || !expandOnClick
            ? PetWindowMoveCompletion.Ignore
            : PetWindowMoveCompletion.Expand;
    }
}

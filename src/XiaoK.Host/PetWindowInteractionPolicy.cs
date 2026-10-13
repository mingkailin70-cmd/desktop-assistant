namespace XiaoK.Host;

internal enum PetWindowMoveCompletion
{
    Ignore,
    Expand,
    SavePosition
}

internal static class PetWindowInteractionPolicy
{
    public static PetWindowMoveCompletion GetCompletion(bool positionChanged, bool cancelled)
    {
        if (positionChanged) return PetWindowMoveCompletion.SavePosition;
        return cancelled ? PetWindowMoveCompletion.Ignore : PetWindowMoveCompletion.Expand;
    }
}

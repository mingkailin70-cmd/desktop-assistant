namespace XiaoK.Host;

internal enum PetWindowMoveCompletion
{
    Ignore,
    Expand,
    SavePosition
}

internal static class PetWindowInteractionPolicy
{
    internal const int HtClient = 1;
    internal const int HtCaption = 2;

    public static int GetHitTest(bool compact, bool resizeHandle) =>
        compact && !resizeHandle ? HtCaption : HtClient;

    public static PetWindowMoveCompletion GetCompletion(bool positionChanged, bool cancelled)
    {
        if (positionChanged) return PetWindowMoveCompletion.SavePosition;
        return cancelled ? PetWindowMoveCompletion.Ignore : PetWindowMoveCompletion.Expand;
    }
}

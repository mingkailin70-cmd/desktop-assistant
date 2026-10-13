namespace XiaoK.Host;

/// <summary>Maps a physical pointer position to a window position during a desktop-pet drag.</summary>
internal readonly record struct PetWindowDragOrigin(
    int CursorX,
    int CursorY,
    int WindowLeft,
    int WindowTop)
{
    public bool TryGetTargetPosition(int cursorX, int cursorY, out int left, out int top)
    {
        var targetLeft = (long)WindowLeft + cursorX - CursorX;
        var targetTop = (long)WindowTop + cursorY - CursorY;
        if (targetLeft is < int.MinValue or > int.MaxValue
            || targetTop is < int.MinValue or > int.MaxValue)
        {
            left = 0;
            top = 0;
            return false;
        }

        left = (int)targetLeft;
        top = (int)targetTop;
        return true;
    }
}

namespace XiaoK.Host;

/// <summary>Separates a deliberate pet-window drag from a click on the pet.</summary>
internal sealed class PetWindowDragGesture
{
    private (int X, int Y)? _pointerDown;

    public bool IsDragging { get; private set; }

    public void PointerDown(int x, int y)
    {
        IsDragging = false;
        _pointerDown = (x, y);
    }

    public bool TryStartDrag(int x, int y, bool leftButtonPressed,
        double minimumHorizontalDistance, double minimumVerticalDistance)
    {
        if (_pointerDown is not { } origin) return false;
        if (!leftButtonPressed)
        {
            Reset();
            return false;
        }

        if (IsDragging) return false;

        var horizontalThreshold = NormalizeThreshold(minimumHorizontalDistance);
        var verticalThreshold = NormalizeThreshold(minimumVerticalDistance);
        if (Math.Abs((long)x - origin.X) < horizontalThreshold
            && Math.Abs((long)y - origin.Y) < verticalThreshold) return false;

        IsDragging = true;
        return true;
    }

    public bool PointerUp()
    {
        var wasDragged = IsDragging;
        Reset();
        return wasDragged;
    }

    public void Reset()
    {
        _pointerDown = null;
        IsDragging = false;
    }

    private static double NormalizeThreshold(double value) =>
        double.IsFinite(value) && value > 0 ? value : 4;
}

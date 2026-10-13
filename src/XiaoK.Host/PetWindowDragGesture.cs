namespace XiaoK.Host;

/// <summary>Separates a deliberate pet-window drag from a click on the pet.</summary>
internal sealed class PetWindowDragGesture
{
    private (double X, double Y)? _pointerDown;
    private bool _dragStarted;

    public void PointerDown(double x, double y)
    {
        _dragStarted = false;
        _pointerDown = double.IsFinite(x) && double.IsFinite(y) ? (X: x, Y: y) : null;
    }

    public bool TryStartDrag(double x, double y, bool leftButtonPressed,
        double minimumHorizontalDistance, double minimumVerticalDistance)
    {
        if (_pointerDown is not { } origin) return false;
        if (!leftButtonPressed || !double.IsFinite(x) || !double.IsFinite(y))
        {
            Reset();
            return false;
        }

        if (_dragStarted) return false;

        var horizontalThreshold = NormalizeThreshold(minimumHorizontalDistance);
        var verticalThreshold = NormalizeThreshold(minimumVerticalDistance);
        if (Math.Abs(x - origin.X) < horizontalThreshold
            && Math.Abs(y - origin.Y) < verticalThreshold) return false;

        _dragStarted = true;
        return true;
    }

    public bool PointerUp()
    {
        var wasDragged = _dragStarted;
        Reset();
        return wasDragged;
    }

    public void Reset()
    {
        _pointerDown = null;
        _dragStarted = false;
    }

    private static double NormalizeThreshold(double value) =>
        double.IsFinite(value) && value > 0 ? value : 4;
}

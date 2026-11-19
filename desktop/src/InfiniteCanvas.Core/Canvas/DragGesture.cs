namespace InfiniteCanvas.Core.Canvas;

/// <summary>Tells a click from a drag on the empty canvas: a right press that stays put opens the canvas menu, one that moves pans the canvas.</summary>
public static class DragGesture
{
    /// <summary>Screen distance in DIPs a pointer must travel after pressing before it counts as a drag.</summary>
    public const double Threshold = 4;

    public static bool IsDrag(double dx, double dy) => dx * dx + dy * dy > Threshold * Threshold;
}

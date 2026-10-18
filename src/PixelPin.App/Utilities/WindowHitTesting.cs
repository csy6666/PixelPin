using System.Windows;

namespace PixelPin.Utilities;

/// <summary>
/// Picks the window under a physical desktop point from a list already ordered
/// top-most first. Pure geometry so the z-order rule can be unit tested.
/// </summary>
public static class WindowHitTesting
{
    /// <summary>Index of the first window containing the point, or -1 when the point is on the bare desktop.</summary>
    public static int TopmostIndexAt(IReadOnlyList<Rect> windows, double x, double y)
    {
        for (var index = 0; index < windows.Count; index++)
        {
            if (Contains(windows[index], x, y))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rectangle containment where the right and bottom edges count as inside, so a
    /// one-pixel-wide window can still be selected.
    /// </summary>
    public static bool Contains(Rect bounds, double x, double y)
    {
        return bounds.Width > 0
            && bounds.Height > 0
            && x >= bounds.Left
            && y >= bounds.Top
            && x <= bounds.Right
            && y <= bounds.Bottom;
    }
}

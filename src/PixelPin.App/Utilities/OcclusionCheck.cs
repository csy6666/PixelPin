using System.Windows;

namespace PixelPin.Utilities;

/// <summary>
/// Works out how much of a window is actually visible on screen, given the windows
/// stacked above it. This decides which capture route is correct: a fully visible window
/// is best read straight from the screen, because that is exactly what the user sees,
/// while an occluded window has to be asked to render itself.
/// </summary>
public static class OcclusionCheck
{
    /// <summary>
    /// Fraction of <paramref name="target"/> that is not covered by
    /// <paramref name="windowsAbove"/>, from 0 to 1. The windows are assumed to be
    /// opaque rectangles, which is true for the purposes of this decision: a layered or
    /// transparent window still cannot be read through by a screen capture.
    /// </summary>
    public static double VisibleFraction(Rect target, IReadOnlyList<Rect>? windowsAbove)
    {
        if (target.Width <= 0 || target.Height <= 0)
        {
            return 0;
        }

        if (windowsAbove is null || windowsAbove.Count == 0)
        {
            return 1;
        }

        // Subtract each covering rectangle from what is left. Exact rectangle subtraction
        // would need polygon clipping; sampling a grid is both simpler and sufficient,
        // because the answer only chooses between two capture routes.
        const int gridX = 24;
        const int gridY = 24;
        var stepX = target.Width / gridX;
        var stepY = target.Height / gridY;

        long total = 0;
        long visible = 0;
        for (var row = 0; row < gridY; row++)
        {
            for (var column = 0; column < gridX; column++)
            {
                // Sample cell centres so a window edge landing exactly on a boundary does
                // not flip a whole row.
                var x = target.Left + ((column + 0.5) * stepX);
                var y = target.Top + ((row + 0.5) * stepY);
                total++;

                var covered = false;
                foreach (var above in windowsAbove)
                {
                    if (above.Width <= 0 || above.Height <= 0)
                    {
                        continue;
                    }

                    if (x >= above.Left && x < above.Right && y >= above.Top && y < above.Bottom)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    visible++;
                }
            }
        }

        return total == 0 ? 0 : visible / (double)total;
    }

    /// <summary>
    /// True when the window is visible enough that reading it from the screen gives the
    /// user what they can see. A small sliver of occlusion is tolerated, because asking
    /// the window to render itself can differ subtly from the composited screen.
    /// </summary>
    public static bool IsMostlyVisible(Rect target, IReadOnlyList<Rect>? windowsAbove, double minimumFraction = 0.98)
        => VisibleFraction(target, windowsAbove) >= minimumFraction;

    /// <summary>
    /// The windows stacked above the one at <paramref name="targetIndex"/> in a
    /// top-most-first list. Returns an empty list when the target is already on top.
    /// </summary>
    public static IReadOnlyList<Rect> WindowsAbove(IReadOnlyList<Rect>? zOrdered, int targetIndex)
    {
        if (zOrdered is null || targetIndex <= 0 || targetIndex >= zOrdered.Count)
        {
            return [];
        }

        var above = new List<Rect>(targetIndex);
        for (var index = 0; index < targetIndex; index++)
        {
            above.Add(zOrdered[index]);
        }

        return above;
    }
}

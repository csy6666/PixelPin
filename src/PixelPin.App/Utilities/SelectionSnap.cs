using System.Windows;

namespace PixelPin.Utilities;

/// <summary>
/// Aligns a free-hand selection rectangle with nearby reference edges, the way
/// Snipaste and PixPin pull a dragged region onto window borders.
/// </summary>
public static class SelectionSnap
{
    public const double DefaultTolerance = 8;

    /// <summary>
    /// Returns <paramref name="candidate"/> with each edge pulled onto the closest
    /// reference edge within <paramref name="tolerance"/>. Edges without a nearby
    /// reference keep their original position.
    /// </summary>
    public static Rect Snap(Rect candidate, IReadOnlyList<Rect>? guides, double tolerance = DefaultTolerance)
    {
        if (guides is null || guides.Count == 0 || tolerance <= 0 || candidate.Width <= 0 || candidate.Height <= 0)
        {
            return candidate;
        }

        var horizontal = new List<double>(guides.Count * 2);
        var vertical = new List<double>(guides.Count * 2);
        foreach (var guide in guides)
        {
            horizontal.Add(guide.Left);
            horizontal.Add(guide.Right);
            vertical.Add(guide.Top);
            vertical.Add(guide.Bottom);
        }

        var left = candidate.Left;
        var top = candidate.Top;
        var right = candidate.Right;
        var bottom = candidate.Bottom;

        if (TryFindDelta(left, horizontal, tolerance, out var leftDelta))
        {
            left += leftDelta;
        }

        if (TryFindDelta(right, horizontal, tolerance, out var rightDelta))
        {
            right += rightDelta;
        }

        if (TryFindDelta(top, vertical, tolerance, out var topDelta))
        {
            top += topDelta;
        }

        if (TryFindDelta(bottom, vertical, tolerance, out var bottomDelta))
        {
            bottom += bottomDelta;
        }

        // Two opposite edges can be pulled across each other on a very thin drag.
        if (right - left < 1)
        {
            if (Math.Abs(rightDelta) > 0)
            {
                left = right - 1;
            }
            else
            {
                right = left + 1;
            }
        }

        if (bottom - top < 1)
        {
            if (Math.Abs(bottomDelta) > 0)
            {
                top = bottom - 1;
            }
            else
            {
                bottom = top + 1;
            }
        }

        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    /// <summary>
    /// Snaps an entire rectangle onto a reference rectangle when every edge is
    /// already close to it. Used to make a hovered window selection pixel exact.
    /// </summary>
    public static Rect SnapToContaining(Rect candidate, IReadOnlyList<Rect>? guides, double tolerance = DefaultTolerance)
    {
        if (guides is null || guides.Count == 0)
        {
            return candidate;
        }

        var best = candidate;
        var bestScore = double.MaxValue;
        foreach (var guide in guides)
        {
            var score = Math.Abs(guide.Left - candidate.Left)
                + Math.Abs(guide.Top - candidate.Top)
                + Math.Abs(guide.Right - candidate.Right)
                + Math.Abs(guide.Bottom - candidate.Bottom);
            if (score <= bestScore && score <= tolerance * 4)
            {
                bestScore = score;
                best = guide;
            }
        }

        return best;
    }

    private static bool TryFindDelta(double value, List<double> targets, double tolerance, out double delta)
    {
        delta = 0;
        var found = false;
        var bestDistance = tolerance;

        foreach (var target in targets)
        {
            var candidateDelta = target - value;
            var distance = Math.Abs(candidateDelta);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                delta = candidateDelta;
                found = true;
            }
        }

        return found;
    }
}

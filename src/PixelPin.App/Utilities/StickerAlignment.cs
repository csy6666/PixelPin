using System.Windows;

namespace PixelPin.Utilities;

/// <summary>
/// A line the user can see while dragging, marking where two stickers line up.
/// The segment spans the union of both rectangles so it visibly connects them.
/// </summary>
public readonly record struct AlignmentGuide(bool IsVertical, double Position, double Start, double End);

/// <summary>Snapped rectangle plus the guides that explain the snap.</summary>
public sealed record AlignmentResult(Rect Bounds, IReadOnlyList<AlignmentGuide> Guides)
{
    public static AlignmentResult None(Rect bounds) => new(bounds, Array.Empty<AlignmentGuide>());
}

/// <summary>
/// Aligns a dragged sticker against the other stickers and the screen edges, the way
/// PixPin and Snipaste do. Deliberately free of window types: the whole snap decision
/// is a function of rectangles, so it is covered by the console tests.
/// </summary>
public static class StickerAlignment
{
    /// <summary>
    /// How close two edges must be before they lock together, in DIP.
    /// </summary>
    public const double DefaultThreshold = 6;

    /// <summary>
    /// The alignable positions along one axis, as fractions of the rectangle size, each paired
    /// with the kind of line it may snap to.
    /// </summary>
    /// <remarks>
    /// Edges snap to edges and centres snap to centres. Letting a centre snap to an edge looks
    /// harmless and is not: dragging a sticker near the right edge of the screen pulled its
    /// centre onto that edge, which put half the sticker off screen. The user asked for a small
    /// nudge and got half their picture hidden, so the two kinds are kept apart.
    /// </remarks>
    private static readonly (double Fraction, TargetKind Kind)[] Positions =
    {
        (0, TargetKind.Edge),
        (0.5, TargetKind.Centre),
        (1, TargetKind.Edge),
    };

    /// <summary>
    /// Snaps <paramref name="dragged"/> onto the nearest alignable line within the
    /// threshold. Movement only: alignment never resizes the sticker.
    /// </summary>
    public static AlignmentResult Align(
        Rect dragged,
        IReadOnlyList<Rect>? others,
        Rect boundary,
        double threshold = DefaultThreshold)
    {
        // The screen's own edges and centre lines are both worth snapping to - centring a
        // sticker on the screen is a common wish - but the centre line only accepts the
        // sticker's centre, so a sticker can never be pulled half off the screen by an
        // alignment nudge.
        var vertical = new List<TargetLine>
        {
            new(boundary.Left, boundary.Top, boundary.Bottom, TargetKind.Edge),
            new(boundary.Left + (boundary.Width / 2), boundary.Top, boundary.Bottom, TargetKind.Centre),
            new(boundary.Right, boundary.Top, boundary.Bottom, TargetKind.Edge),
        };

        var horizontal = new List<TargetLine>
        {
            new(boundary.Top, boundary.Left, boundary.Right, TargetKind.Edge),
            new(boundary.Top + (boundary.Height / 2), boundary.Left, boundary.Right, TargetKind.Centre),
            new(boundary.Bottom, boundary.Left, boundary.Right, TargetKind.Edge),
        };

        foreach (var other in others ?? Array.Empty<Rect>())
        {
            // Skip rectangles that cannot be seen, so a minimised or fully hidden
            // sticker never pulls the dragged one to an invisible edge.
            if (other.Width <= 0 || other.Height <= 0)
            {
                continue;
            }

            vertical.Add(new TargetLine(other.Left, other.Top, other.Bottom, TargetKind.Edge));
            vertical.Add(new TargetLine(other.Left + (other.Width / 2), other.Top, other.Bottom, TargetKind.Centre));
            vertical.Add(new TargetLine(other.Right, other.Top, other.Bottom, TargetKind.Edge));

            horizontal.Add(new TargetLine(other.Top, other.Left, other.Right, TargetKind.Edge));
            horizontal.Add(new TargetLine(other.Top + (other.Height / 2), other.Left, other.Right, TargetKind.Centre));
            horizontal.Add(new TargetLine(other.Bottom, other.Left, other.Right, TargetKind.Edge));
        }

        var guides = new List<AlignmentGuide>();
        var bounds = dragged;

        if (TryFindSnap(vertical, dragged.Left, dragged.Width, threshold, out var deltaX, out var verticalLine))
        {
            bounds = new Rect(bounds.Left + deltaX, bounds.Top, bounds.Width, bounds.Height);
            guides.Add(new AlignmentGuide(
                true,
                verticalLine.Position,
                Math.Min(bounds.Top, verticalLine.Start),
                Math.Max(bounds.Bottom, verticalLine.End)));
        }

        if (TryFindSnap(horizontal, dragged.Top, dragged.Height, threshold, out var deltaY, out var horizontalLine))
        {
            bounds = new Rect(bounds.Left, bounds.Top + deltaY, bounds.Width, bounds.Height);
            guides.Add(new AlignmentGuide(
                false,
                horizontalLine.Position,
                Math.Min(bounds.Left, horizontalLine.Start),
                Math.Max(bounds.Right, horizontalLine.End)));
        }

        return new AlignmentResult(bounds, guides);
    }

    /// <summary>
    /// Keeps at least <paramref name="minimumVisible"/> of the sticker inside the desktop on
    /// each axis. A sticker dragged entirely past an edge cannot be grabbed again and there is
    /// no way back except closing it, so the drag stops being useful exactly when the user is
    /// least likely to notice. This is the same courtesy Snipaste extends: you can push a
    /// sticker almost off the screen, never past the point of no return.
    /// </summary>
    public static Rect ClampToDesktop(Rect dragged, Rect boundary, double minimumVisible = 48)
    {
        if (boundary.Width <= 0 || boundary.Height <= 0 || dragged.Width <= 0 || dragged.Height <= 0)
        {
            return dragged;
        }

        var visibleX = Math.Min(minimumVisible, dragged.Width);
        var visibleY = Math.Min(minimumVisible, dragged.Height);

        var left = Math.Clamp(
            dragged.Left,
            boundary.Left - dragged.Width + visibleX,
            boundary.Right - visibleX);
        var top = Math.Clamp(
            dragged.Top,
            boundary.Top - dragged.Height + visibleY,
            boundary.Bottom - visibleY);

        return new Rect(left, top, dragged.Width, dragged.Height);
    }

    /// <summary>
    /// Finds the smallest translation that brings one of the rectangle's alignable positions
    /// onto a matching target line. The two axes are decided independently, which is what lets
    /// a sticker line up on the left edge and the vertical centre at the same time.
    /// </summary>
    private static bool TryFindSnap(
        IReadOnlyList<TargetLine> targets,
        double origin,
        double size,
        double threshold,
        out double delta,
        out TargetLine line)
    {
        delta = 0;
        line = default;
        var best = double.MaxValue;

        foreach (var target in targets)
        {
            foreach (var (fraction, kind) in Positions)
            {
                if (kind != target.Kind)
                {
                    continue;
                }

                var difference = target.Position - (origin + (size * fraction));
                var distance = Math.Abs(difference);
                if (distance <= threshold && distance < best)
                {
                    best = distance;
                    delta = difference;
                    line = target;
                }
            }
        }

        return best < double.MaxValue;
    }

    /// <summary>An alignable line: its position on one axis and its extent on the other.</summary>
    private readonly record struct TargetLine(double Position, double Start, double End, TargetKind Kind);

    /// <summary>Whether a line marks an edge or a centre.</summary>
    private enum TargetKind
    {
        Edge,
        Centre,
    }
}

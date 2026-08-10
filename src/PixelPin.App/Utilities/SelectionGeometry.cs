using System.Windows;

namespace PixelPin.Utilities;

public static class SelectionGeometry
{
    public static Rect Normalize(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        return new Rect(left, top, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    public static Rect Clamp(Rect value, Size boundary, double minimumSize = 1)
    {
        var width = Math.Min(Math.Max(minimumSize, value.Width), boundary.Width);
        var height = Math.Min(Math.Max(minimumSize, value.Height), boundary.Height);
        var left = Math.Clamp(value.Left, 0, Math.Max(0, boundary.Width - width));
        var top = Math.Clamp(value.Top, 0, Math.Max(0, boundary.Height - height));
        return new Rect(left, top, width, height);
    }

    public static Rect Resize(Rect initial, ResizeEdge edge, Vector delta, Size boundary, double minimumSize = 12)
    {
        var left = initial.Left;
        var top = initial.Top;
        var right = initial.Right;
        var bottom = initial.Bottom;

        if (edge.HasFlag(ResizeEdge.Left)) left += delta.X;
        if (edge.HasFlag(ResizeEdge.Right)) right += delta.X;
        if (edge.HasFlag(ResizeEdge.Top)) top += delta.Y;
        if (edge.HasFlag(ResizeEdge.Bottom)) bottom += delta.Y;

        if (right - left < minimumSize)
        {
            if (edge.HasFlag(ResizeEdge.Left)) left = right - minimumSize;
            else right = left + minimumSize;
        }
        if (bottom - top < minimumSize)
        {
            if (edge.HasFlag(ResizeEdge.Top)) top = bottom - minimumSize;
            else bottom = top + minimumSize;
        }

        return Clamp(new Rect(new Point(left, top), new Point(right, bottom)), boundary, minimumSize);
    }
}

[Flags]
public enum ResizeEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

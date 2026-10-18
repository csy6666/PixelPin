using System.Windows;

namespace PixelPin.Utilities;

/// <summary>
/// Size math for sticker windows. Kept free of WPF window types so the
/// aspect-ratio rules can be verified without a desktop session.
/// </summary>
public static class StickerGeometry
{
    public const double MinimumWidth = 80;
    public const double MaximumWidth = 2600;
    public const double MinimumHeight = 40;
    public const double MaximumHeight = 2600;

    /// <summary>
    /// Applies a corner drag to a sticker box. When <paramref name="keepAspect"/> is
    /// set the dominant axis wins and the other axis follows the supplied ratio.
    /// </summary>
    public static Size ResizeFromCorner(
        Size current,
        double aspectRatio,
        Vector delta,
        bool keepAspect)
    {
        var width = current.Width;
        var height = current.Height;

        if (width <= 0 || height <= 0)
        {
            return current;
        }

        if (!keepAspect)
        {
            return ClampFree(new Size(width + delta.X, height + delta.Y));
        }

        var ratio = aspectRatio > 0 ? aspectRatio : width / height;
        var scaleX = (width + delta.X) / width;
        var scaleY = (height + delta.Y) / height;
        var scale = Math.Abs(scaleX - 1) >= Math.Abs(scaleY - 1) ? scaleX : scaleY;

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        var scaledWidth = Math.Clamp(width * scale, MinimumWidth, MaximumWidth);
        var scaledHeight = scaledWidth / ratio;
        if (scaledHeight < MinimumHeight)
        {
            scaledHeight = MinimumHeight;
            scaledWidth = scaledHeight * ratio;
        }
        else if (scaledHeight > MaximumHeight)
        {
            scaledHeight = MaximumHeight;
            scaledWidth = scaledHeight * ratio;
        }

        return new Size(scaledWidth, scaledHeight);
    }

    /// <summary>Scales a sticker box by a factor while preserving the aspect ratio.</summary>
    public static Size Scale(Size current, double aspectRatio, double factor)
    {
        if (current.Width <= 0 || current.Height <= 0 || factor <= 0)
        {
            return current;
        }

        var ratio = aspectRatio > 0 ? aspectRatio : current.Width / current.Height;
        var width = Math.Clamp(current.Width * factor, MinimumWidth, MaximumWidth);
        var height = width / ratio;
        if (height < MinimumHeight)
        {
            height = MinimumHeight;
            width = height * ratio;
        }
        else if (height > MaximumHeight)
        {
            height = MaximumHeight;
            width = height * ratio;
        }

        return new Size(width, height);
    }

    /// <summary>
    /// Natural window size for a freshly pinned image: the image's own pixel size,
    /// reduced so a large capture does not cover the whole desktop.
    /// </summary>
    public static Size InitialSize(int pixelWidth, int pixelHeight, double maximumWidth = 560)
    {
        var width = Math.Clamp((double)pixelWidth, MinimumWidth, Math.Max(MinimumWidth, maximumWidth));
        var height = Math.Max(MinimumHeight, width * pixelHeight / Math.Max(1, pixelWidth));
        return new Size(width, height);
    }

    /// <summary>Aspect ratio a rotated box should use, swapping the ratio on quarter turns.</summary>
    public static double RotatedAspectRatio(double aspectRatio, double rotationDegrees)
    {
        var normalized = ((rotationDegrees % 360) + 360) % 360;
        return Math.Abs(normalized - 90) < 0.5 || Math.Abs(normalized - 270) < 0.5
            ? 1 / aspectRatio
            : aspectRatio;
    }

    /// <summary>
    /// Keeps a sticker reachable after the desktop changes shape. A monitor that was
    /// unplugged, a resolution that shrank, or a remote session that ended can all
    /// leave a window at coordinates no screen covers any more; the window is still
    /// running and still holding its image, but there is nothing to click.
    ///
    /// A window that is already at least partly visible is returned untouched, so a
    /// deliberate overhang - half a sticker off the edge - survives a resolution change.
    /// Only a window with no visible area is pulled back, and then it is moved by the
    /// smallest amount that puts a corner on screen.
    /// </summary>
    public static Rect ClampIntoDesktop(Rect window, Rect desktop)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || window.Width <= 0 || window.Height <= 0)
        {
            return window;
        }

        // Any overlap at all means the user can still grab it.
        var visible = Rect.Intersect(window, desktop);
        if (!visible.IsEmpty && visible.Width >= MinimumWidth / 2 && visible.Height >= MinimumHeight / 2)
        {
            return window;
        }

        // Prefer the last known offset when the desktop grew back, otherwise pin the
        // window inside the new bounds. Either way the whole window ends up visible.
        var width = Math.Min(window.Width, desktop.Width);
        var height = Math.Min(window.Height, desktop.Height);
        var left = Math.Clamp(window.Left, desktop.Left, desktop.Right - width);
        var top = Math.Clamp(window.Top, desktop.Top, desktop.Bottom - height);
        return new Rect(left, top, window.Width, window.Height);
    }

    private static Size ClampFree(Size size)
    {
        return new Size(
            Math.Clamp(size.Width, MinimumWidth, MaximumWidth),
            Math.Clamp(size.Height, MinimumHeight, MaximumHeight));
    }
}

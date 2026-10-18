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

    private static Size ClampFree(Size size)
    {
        return new Size(
            Math.Clamp(size.Width, MinimumWidth, MaximumWidth),
            Math.Clamp(size.Height, MinimumHeight, MaximumHeight));
    }
}

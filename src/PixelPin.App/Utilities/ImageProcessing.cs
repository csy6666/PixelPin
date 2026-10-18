using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelPin.Utilities;

/// <summary>
/// Pixel-level redaction helpers. Both operations work on a copied buffer so the
/// exported image matches the editor preview exactly, and so they can be verified
/// without a desktop session.
/// </summary>
public static class ImageProcessing
{
    public const int MinimumBlockSize = 3;
    public const int MaximumBlockSize = 40;
    public const int MinimumBlurRadius = 1;
    public const int MaximumBlurRadius = 30;

    /// <summary>Replaces the region with averaged colour blocks.</summary>
    public static BitmapSource Pixelate(BitmapSource source, Int32Rect region, int blockSize)
    {
        var buffer = ReadRegion(source, region, out var width, out var height);
        var block = Math.Clamp(blockSize, MinimumBlockSize, MaximumBlockSize);

        for (var blockTop = 0; blockTop < height; blockTop += block)
        {
            var blockBottom = Math.Min(blockTop + block, height);
            for (var blockLeft = 0; blockLeft < width; blockLeft += block)
            {
                var blockRight = Math.Min(blockLeft + block, width);

                long blue = 0;
                long green = 0;
                long red = 0;
                long alpha = 0;
                var count = 0;

                for (var y = blockTop; y < blockBottom; y++)
                {
                    var row = y * width * 4;
                    for (var x = blockLeft; x < blockRight; x++)
                    {
                        var index = row + (x * 4);
                        blue += buffer[index];
                        green += buffer[index + 1];
                        red += buffer[index + 2];
                        alpha += buffer[index + 3];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var averageBlue = (byte)(blue / count);
                var averageGreen = (byte)(green / count);
                var averageRed = (byte)(red / count);
                var averageAlpha = (byte)(alpha / count);

                for (var y = blockTop; y < blockBottom; y++)
                {
                    var row = y * width * 4;
                    for (var x = blockLeft; x < blockRight; x++)
                    {
                        var index = row + (x * 4);
                        buffer[index] = averageBlue;
                        buffer[index + 1] = averageGreen;
                        buffer[index + 2] = averageRed;
                        buffer[index + 3] = averageAlpha;
                    }
                }
            }
        }

        return CreateRegion(buffer, region, width, height, source);
    }

    /// <summary>Box-blurs the region. Alpha is preserved so the result stays opaque.</summary>
    public static BitmapSource BoxBlur(BitmapSource source, Int32Rect region, int radius)
    {
        var buffer = ReadRegion(source, region, out var width, out var height);
        var clamped = Math.Clamp(radius, MinimumBlurRadius, MaximumBlurRadius);
        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = buffer[(i * 4) + 3];
        }

        var horizontal = new byte[buffer.Length];
        var scratch = new int[3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Accumulate(buffer, width, height, x, y, clamped, scratch);
                var index = ((y * width) + x) * 4;
                horizontal[index] = (byte)(scratch[0] / ((2 * clamped) + 1) / ((2 * clamped) + 1));
                horizontal[index + 1] = (byte)(scratch[1] / ((2 * clamped) + 1) / ((2 * clamped) + 1));
                horizontal[index + 2] = (byte)(scratch[2] / ((2 * clamped) + 1) / ((2 * clamped) + 1));
                horizontal[index + 3] = alpha[(y * width) + x];
            }
        }

        return CreateRegion(horizontal, region, width, height, source);
    }

    private static void Accumulate(byte[] buffer, int width, int height, int x, int y, int radius, int[] totals)
    {
        long blue = 0;
        long green = 0;
        long red = 0;

        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        {
            var sampleY = Math.Clamp(y + offsetY, 0, height - 1);
            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                var sampleX = Math.Clamp(x + offsetX, 0, width - 1);
                var index = ((sampleY * width) + sampleX) * 4;
                blue += buffer[index];
                green += buffer[index + 1];
                red += buffer[index + 2];
            }
        }

        var divisor = (2 * radius) + 1;
        totals[0] = (int)(blue / divisor);
        totals[1] = (int)(green / divisor);
        totals[2] = (int)(red / divisor);
    }

    private static byte[] ReadRegion(BitmapSource source, Int32Rect region, out int width, out int height)
    {
        var bounds = new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);
        var safe = Intersect(region, bounds);
        if (safe.Width < 1 || safe.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "标注区域不在图像范围内。");
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        width = safe.Width;
        height = safe.Height;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(safe, buffer, stride, 0);
        return buffer;
    }

    private static BitmapSource CreateRegion(byte[] buffer, Int32Rect region, int width, int height, BitmapSource source)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            buffer,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Int32Rect Intersect(Int32Rect left, Int32Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new Int32Rect(x, y, Math.Max(0, rightEdge - x), Math.Max(0, bottomEdge - y));
    }
}

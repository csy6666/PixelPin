using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelPin.Interop;

namespace PixelPin.Utilities;

public static class BitmapUtilities
{
    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    /// <summary>
    /// Copies a bitmap into a tightly described BGRA buffer. Row pitch is returned
    /// separately because it is usually wider than the row itself, and callers that index
    /// by pixel must honour it.
    /// </summary>
    public static byte[] ToBgraBuffer(BitmapSource source, out int stride, out int width, out int height)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        width = converted.PixelWidth;
        height = converted.PixelHeight;
        stride = width * 4;

        var buffer = new byte[(long)stride * height];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>Builds a frozen bitmap from a BGRA buffer without copying it again.</summary>
    public static BitmapSource FromBgraBuffer(byte[] buffer, int width, int height, int stride)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        image.Freeze();
        return image;
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect pixels)
    {
        if (pixels.Width < 1 || pixels.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "所选截图区域为空。");
        }

        var bgraSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var crop = new CroppedBitmap(bgraSource, pixels);
        crop.Freeze();

        var stride = checked(pixels.Width * 4);
        var buffer = new byte[checked(stride * pixels.Height)];
        crop.CopyPixels(buffer, stride, 0);

        var detached = BitmapSource.Create(
            pixels.Width,
            pixels.Height,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            buffer,
            stride);
        detached.Freeze();
        return detached;
    }

    public static void CopyToClipboard(BitmapSource image)
    {
        Clipboard.SetImage(image);
    }

    public static string SavePng(BitmapSource image, string directory, string template)
    {
        Directory.CreateDirectory(directory);
        var path = GetUniquePath(directory, BuildFileName(template, DateTime.Now), ".png");
        SavePngToPath(image, path);
        return path;
    }

    public static string SaveJpeg(BitmapSource image, string directory, string template, int quality = 92)
    {
        Directory.CreateDirectory(directory);
        var path = GetUniquePath(directory, BuildFileName(template, DateTime.Now), ".jpg");
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public static string SaveJpegToPath(BitmapSource image, string path, int quality = 92)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public static void SavePngToPath(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static string BuildFileName(string template, DateTime now)
    {
        var name = template
            .Replace("{yyyyMMdd_HHmmss}", now.ToString("yyyyMMdd_HHmmss"), StringComparison.Ordinal)
            .Replace("{yyyy-MM-dd}", now.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            .Replace("{HHmmss}", now.ToString("HHmmss"), StringComparison.Ordinal);

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "PixelPin" : name;
    }

    private static string GetUniquePath(string directory, string name, string extension)
    {
        var path = Path.Combine(directory, name + extension);
        var suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{name}_{suffix++}{extension}");
        }

        return path;
    }
}

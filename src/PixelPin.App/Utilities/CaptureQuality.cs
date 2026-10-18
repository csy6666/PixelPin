using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelPin.Utilities;

/// <summary>Why a window capture was rejected, or <see cref="None"/> when it is usable.</summary>
public enum CaptureRejection
{
    None = 0,

    /// <summary>Every sampled pixel was identical, so the window rendered nothing.</summary>
    Uniform,

    /// <summary>Almost every pixel was black, which is how a failed window render looks.</summary>
    Black,

    /// <summary>The image was empty or could not be sampled.</summary>
    Empty,
}

/// <summary>
/// Decides whether a captured window image actually contains the window. Rendering a
/// window directly can silently produce a black or blank bitmap for hardware-accelerated,
/// protected, or mid-repaint windows, and accepting that would hand the user a black
/// screenshot while claiming success. The decision is a pure function of pixel data so it
/// is unit tested rather than tuned against a live desktop.
/// </summary>
public static class CaptureQuality
{
    /// <summary>Fraction of sampled pixels that must be non-black for the image to pass.</summary>
    public const double MinimumNonBlackRatio = 0.02;

    /// <summary>Distinct colours required before an image is considered to have content.</summary>
    public const int MinimumDistinctColours = 4;

    /// <summary>Fraction of sampled pixels that must be fully transparent to fail.</summary>
    public const double MaximumTransparentRatio = 0.98;

    /// <summary>Longest edge of the sampling grid, keeping the check cheap on 4K windows.</summary>
    public const int SampleGridSize = 96;

    /// <summary>Luminance at or below which a pixel counts as black.</summary>
    public const int BlackLuminanceThreshold = 8;

    /// <summary>
    /// Inspects an image and reports whether it is usable. <paramref name="expectedSize"/>
    /// is the window size the capture was supposed to produce; a mismatch means the
    /// window was resized mid-capture or the render returned a differently sized surface.
    /// <para>
    /// <paramref name="renderReportedSuccess"/> is what PrintWindow itself returned. A flat
    /// image is ambiguous on its own: it is what an unrendered window looks like, but it is
    /// also what a genuinely blank window looks like. The platform's own answer breaks the
    /// tie, so a flat window is only refused when the render also reported failure. Pure
    /// black is refused either way, because that is the signature of a window that cannot
    /// be rendered at all.
    /// </para>
    /// </summary>
    public static CaptureRejection Inspect(
        BitmapSource? image,
        Size? expectedSize = null,
        bool renderReportedSuccess = true)
    {
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            return CaptureRejection.Empty;
        }

        if (expectedSize is { } expected
            && (Math.Abs(image.PixelWidth - expected.Width) > 2 || Math.Abs(image.PixelHeight - expected.Height) > 2))
        {
            return CaptureRejection.Empty;
        }

        var converted = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        var stepX = Math.Max(1, converted.PixelWidth / SampleGridSize);
        var stepY = Math.Max(1, converted.PixelHeight / SampleGridSize);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var colours = new HashSet<int>();
        long sampled = 0;
        long nonBlack = 0;
        long transparent = 0;

        for (var y = 0; y < converted.PixelHeight; y += stepY)
        {
            for (var x = 0; x < converted.PixelWidth; x += stepX)
            {
                var index = (y * stride) + (x * 4);
                var blue = buffer[index];
                var green = buffer[index + 1];
                var red = buffer[index + 2];
                var alpha = buffer[index + 3];

                sampled++;
                if (alpha < 8)
                {
                    transparent++;
                }

                // Rec. 601 luma, matching how the dimmer check reads brightness elsewhere.
                var luminance = ((red * 299) + (green * 587) + (blue * 114)) / 1000;
                if (luminance > BlackLuminanceThreshold)
                {
                    nonBlack++;
                }

                colours.Add((red << 16) | (green << 8) | blue);
            }
        }

        if (sampled == 0)
        {
            return CaptureRejection.Empty;
        }

        if (transparent / (double)sampled >= MaximumTransparentRatio)
        {
            return CaptureRejection.Empty;
        }

        // Order matters for the message the user sees. An all-black render is also a
        // single-colour render, but "the window is hardware accelerated" is far more
        // useful than "the window is one colour", so the black test runs first.
        if (nonBlack / (double)sampled < MinimumNonBlackRatio)
        {
            return CaptureRejection.Black;
        }

        // A single colour usually means the window rendered nothing, but a genuinely blank
        // window looks the same. Only refuse it when the platform also reported failure,
        // which keeps a real blank window capturable.
        if (!renderReportedSuccess && colours.Count < MinimumDistinctColours)
        {
            return CaptureRejection.Uniform;
        }

        return CaptureRejection.None;
    }

    /// <summary>True when the image is usable.</summary>
    public static bool IsUsable(BitmapSource? image, Size? expectedSize = null, bool renderReportedSuccess = true)
        => Inspect(image, expectedSize, renderReportedSuccess) == CaptureRejection.None;

    /// <summary>Message explaining a rejection, or null when the image passed.</summary>
    public static string? Describe(CaptureRejection rejection) => rejection switch
    {
        CaptureRejection.None => null,
        CaptureRejection.Empty => "窗口没有返回可用的画面。",
        CaptureRejection.Uniform => "窗口返回了纯色画面，通常表示该窗口无法被直接渲染。",
        CaptureRejection.Black => "窗口返回了全黑画面，通常表示该窗口使用硬件加速或受保护内容。",
        _ => "窗口画面不可用。",
    };
}

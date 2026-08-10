using System.Windows.Media.Imaging;

namespace PixelPin.Models;

public sealed record CaptureOutcome(BitmapSource Image, string? SuggestedFileName = null);

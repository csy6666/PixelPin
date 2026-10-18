using System.Drawing;

namespace PixelPin.Models;

public sealed record CaptureWindowDescriptor(
    nint Handle,
    string Title,
    string ProcessName,
    Rectangle Bounds,
    string ClassName = "")
{
    public string DisplayName => $"{Title}（{ProcessName}）- {Bounds.Width} x {Bounds.Height}";

    /// <summary>Detection-only descriptor without the process lookup cost.</summary>
    public static CaptureWindowDescriptor Detection(nint handle, string title, string className, Rectangle bounds)
        => new(handle, title, string.Empty, bounds, className);
}

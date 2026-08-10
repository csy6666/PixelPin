using System.Drawing;

namespace PixelPin.Models;

public sealed record CaptureWindowDescriptor(
    nint Handle,
    string Title,
    string ProcessName,
    Rectangle Bounds)
{
    public string DisplayName => $"{Title} ({ProcessName}) - {Bounds.Width} x {Bounds.Height}";
}

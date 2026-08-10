using System.Drawing;
using System.Windows.Media.Imaging;

namespace PixelPin.Models;

public sealed record CapturedDesktop(BitmapSource Image, Rectangle Bounds);

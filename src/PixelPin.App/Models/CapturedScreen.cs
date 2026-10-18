using System.Drawing;
using System.Windows.Media.Imaging;

namespace PixelPin.Models;

public sealed record CapturedScreen(BitmapSource Image, Rectangle Bounds);

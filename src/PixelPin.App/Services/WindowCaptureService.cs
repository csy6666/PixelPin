using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Utilities;
using WpfSize = System.Windows.Size;
namespace PixelPin.Services;

/// <summary>How a window image was obtained.</summary>
public enum WindowCaptureMethod
{
    /// <summary>
    /// Windows.Graphics.Capture read the window's own composed surface, so occlusion,
    /// hardware acceleration, and rounded corners all came out right.
    /// </summary>
    GraphicsCapture,

    /// <summary>The window was asked to render itself, so occlusion did not matter.</summary>
    Render,

    /// <summary>The window was read from the captured screen, which is what the user sees.</summary>
    Screen,
}

/// <summary>Result of a window capture, including why it failed when it did.</summary>
public sealed record WindowCaptureResult(
    BitmapSource? Image,
    WindowCaptureMethod Method,
    string? Failure)
{
    public bool Succeeded => Image is not null;
}

/// <summary>
/// Captures a single window. A window covered by another window cannot be read from the
/// screen, so it is asked to render itself through PrintWindow with PW_RENDERFULLCONTENT.
/// That render can silently come back black for hardware-accelerated or protected windows,
/// so every result is inspected before it is accepted and the caller is told which route
/// produced it.
/// </summary>
public static class WindowCaptureService
{
    /// <summary>
    /// Captures a window by walking the route order the planner chose, and reports every
    /// route that was refused. <paramref name="screenCrop"/> receives the window rectangle
    /// in physical virtual-desktop coordinates and returns the cropped desktop image, or
    /// null when that rectangle is not on the desktop.
    /// </summary>
    public static WindowCaptureResult TryCaptureWindow(
        nint handle,
        WindowCaptureConditions conditions,
        Func<System.Windows.Int32Rect, BitmapSource?> screenCrop)
    {
        var plan = WindowCapturePlanner.Build(conditions);
        if (plan.IsRefused)
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.Render, plan.Refusal);
        }

        var failures = new List<string>();

        foreach (var route in plan.Routes)
        {
            switch (route)
            {
                case WindowCaptureRoute.GraphicsCapture:
                {
                    var captured = WgcCaptureService.TryCapture(handle);
                    if (captured.Succeeded)
                    {
                        return captured;
                    }

                    failures.Add(Describe(route, captured.Failure));
                    break;
                }

                case WindowCaptureRoute.Render:
                {
                    var rendered = TryRenderWindow(handle);
                    if (rendered.Succeeded)
                    {
                        return rendered;
                    }

                    failures.Add(Describe(route, rendered.Failure));
                    break;
                }

                case WindowCaptureRoute.Screen:
                {
                    var bounds = GetCaptureBounds(handle);
                    var image = bounds.Width > 0 && bounds.Height > 0 ? screenCrop(bounds) : null;
                    if (image is not null)
                    {
                        return new WindowCaptureResult(image, WindowCaptureMethod.Screen, null);
                    }

                    failures.Add(Describe(route, "窗口不在可截图的桌面范围内。"));
                    break;
                }
            }
        }

        // Every route is reported, because "the window could not be captured" is far less
        // actionable than knowing that the GPU refused, the window would not render, and it
        // is not on the desktop.
        return new WindowCaptureResult(
            null,
            WindowCaptureMethod.Render,
            failures.Count > 0
                ? "无法截取该窗口：" + string.Join("；", failures)
                : "无法截取该窗口。");
    }

    private static string Describe(WindowCaptureRoute route, string? failure)
    {
        var name = route switch
        {
            WindowCaptureRoute.GraphicsCapture => "图形捕获",
            WindowCaptureRoute.Render => "窗口自绘",
            _ => "屏幕裁剪",
        };

        return string.IsNullOrWhiteSpace(failure) ? $"{name}失败" : $"{name}：{failure}";
    }

    /// <summary>
    /// Renders a window into a bitmap. Returns a failure reason rather than a black image
    /// when the window cannot render itself.
    /// </summary>
    public static WindowCaptureResult TryRenderWindow(nint handle)
    {
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle))
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.Render, "窗口已关闭。");
        }

        var bounds = GetCaptureBounds(handle);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.Render, "窗口当前没有可截取的区域（可能已最小化）。");
        }

        var expected = new WpfSize(bounds.Width, bounds.Height);

        try
        {
            // Format32bppArgb keeps the GDI+ surface usable, but GDI drawing does not
            // maintain the alpha byte, so the result is forced opaque below. Without that
            // step the render arrives fully transparent and would show as an empty image.
            bool printed;
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        // Some windows report failure while still having drawn, so this value
                        // is passed to the inspection as evidence rather than treated as the
                        // verdict on its own.
                        printed = NativeMethods.PrintWindow(handle, hdc, NativeMethods.PwRenderFullContent);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                ForceOpaque(bitmap);
                var image = BitmapUtilities.ToBitmapSource(bitmap);
                var quality = CaptureQuality.Inspect(image, expected, printed);

                return quality == CaptureRejection.None
                    ? new WindowCaptureResult(image, WindowCaptureMethod.Render, null)
                    : new WindowCaptureResult(
                        null,
                        WindowCaptureMethod.Render,
                        CaptureQuality.Describe(quality) ?? "窗口没有返回可用的画面。");
            }
        }
        catch (Exception exception)
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.Render, $"无法渲染该窗口：{exception.Message}");
        }
    }

    /// <summary>
    /// The rectangle a window render should cover: the DWM extended frame bounds when
    /// available, because GetWindowRect includes the invisible resize border and would
    /// otherwise add a transparent margin around every capture.
    /// </summary>
    public static System.Windows.Int32Rect GetCaptureBounds(nint handle)
    {
        var result = NativeMethods.DwmGetWindowAttributeRect(
            handle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out var extended,
            Marshal.SizeOf<NativeRect>());

        NativeRect rectangle;
        if (result == 0 && extended.Width > 0 && extended.Height > 0)
        {
            rectangle = extended;
        }
        else if (!NativeMethods.GetWindowRect(handle, out rectangle))
        {
            return new System.Windows.Int32Rect(0, 0, 0, 0);
        }

        return new System.Windows.Int32Rect(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);
    }

    /// <summary>
    /// Sets every alpha byte to opaque. GDI drawing into a 32-bit surface leaves the alpha
    /// channel at zero, and a zero alpha makes the whole image invisible once it reaches
    /// WPF, so this has to be corrected before the bitmap is converted.
    /// </summary>
    private static void ForceOpaque(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var index = 3; index < buffer.Length; index += 4)
            {
                buffer[index] = 0xFF;
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

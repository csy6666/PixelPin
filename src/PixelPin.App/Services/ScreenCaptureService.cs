using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Models;
using PixelPin.Utilities;

namespace PixelPin.Services;

public sealed class ScreenCaptureService
{
    public CapturedDesktop CaptureDesktop()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Windows 没有报告可用的桌面。");
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return new CapturedDesktop(BitmapUtilities.ToBitmapSource(bitmap), bounds);
    }

    public CaptureOutcome CaptureForegroundWindow()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.IsIconic(window))
        {
            throw new InvalidOperationException("没有可截图的可见前台窗口。");
        }

        return CaptureWindowHandle(window, "PixelPin_window_{yyyyMMdd_HHmmss}");
    }

    public IReadOnlyList<CaptureWindowDescriptor> EnumerateCaptureWindows()
    {
        var windows = new List<CaptureWindowDescriptor>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
                {
                    return true;
                }

                var titleLength = NativeMethods.GetWindowTextLength(handle);
                if (titleLength <= 0 || !NativeMethods.GetWindowRect(handle, out var bounds)
                    || bounds.Width < 32 || bounds.Height < 32)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(handle, out var processId);
                if (processId == Environment.ProcessId)
                {
                    return true;
                }

                var builder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(handle, builder, builder.Capacity);
                var title = builder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                var processName = GetProcessName(processId);
                windows.Add(new CaptureWindowDescriptor(
                    handle,
                    title,
                    processName,
                    new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height)));
            }
            catch
            {
                // A window can disappear while EnumWindows is walking the desktop.
            }

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public CaptureOutcome CaptureWindow(CaptureWindowDescriptor window)
    {
        if (window.Handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(window.Handle) || NativeMethods.IsIconic(window.Handle))
        {
            throw new InvalidOperationException("所选窗口已不可用，请刷新列表后重新选择。");
        }

        return CaptureWindowHandle(window.Handle, "PixelPin_window_{yyyyMMdd_HHmmss}");
    }

    private CaptureOutcome CaptureWindowHandle(nint window, string suggestedFileName)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException("Windows 无法确定当前窗口边界。");
        }

        var desktop = CaptureDesktop();
        var crop = new Int32Rect(
            rect.Left - desktop.Bounds.Left,
            rect.Top - desktop.Bounds.Top,
            rect.Width,
            rect.Height);

        crop = Intersect(crop, new Int32Rect(0, 0, desktop.Image.PixelWidth, desktop.Image.PixelHeight));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("当前窗口位于可截图桌面区域之外。");
        }

        return new CaptureOutcome(BitmapUtilities.Crop(desktop.Image, crop), suggestedFileName);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return "未知进程";
        }
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
